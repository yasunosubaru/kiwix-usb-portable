using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KiwixWinUI;

public enum PreflightState { Ok, Warn, Fail, Unknown }

public sealed record PreflightItem(string Key, string Label, PreflightState State, string Detail)
{
    public bool Ok => State == PreflightState.Ok;
}

public sealed record ZimEntry(string Name, long Size, string Integrity, PreflightState IntegrityState)
{
    public string SizeText => BundleLayout.HumanSize(Size);
}

/// <summary>
/// Owns the kiwix-serve child process. Everything here is deliberately
/// conservative: never kill a foreign process, never elevate, never write
/// to the ZIM files, never reach the network beyond localhost.
/// </summary>
public sealed class KiwixService : IDisposable
{
    private Process? _proc;
    private readonly Dictionary<string, PreflightItem> _preflight = new();
    private readonly Dictionary<string, (string Text, PreflightState State)> _integrity = new();

    /// <summary>How long to wait for the server to answer before giving up on a clean start.</summary>
    private const int StartupProbeSeconds = 25;

    public event Action<string>? LogLine;
    public event Action? Stopped;
    public event Action<int>? Exited;

    public bool IsRunning => _proc is { HasExited: false };
    public int? ProcessId => _proc is { HasExited: false } p ? p.Id : null;
    public int ActivePort { get; private set; }

    public IReadOnlyList<ZimEntry> Library()
    {
        var result = new List<ZimEntry>();
        foreach (var (name, size) in BundleLayout.ListZims())
        {
            if (_integrity.TryGetValue(name, out var v))
                result.Add(new ZimEntry(name, size, v.Text, v.State));
            else
                result.Add(new ZimEntry(name, size, "未校验", PreflightState.Unknown));
        }
        return result;
    }

    public long LibraryTotalBytes => BundleLayout.ListZims().Sum(z => z.Size);

    // ---------------------------------------------------------------- preflight

    public IReadOnlyList<PreflightItem> Preflight(int port)
    {
        var items = new List<PreflightItem>();

        items.Add(BundleLayout.IsPlatformSupported
            ? new("arch", "架构", PreflightState.Ok, BundleLayout.PlatformKey)
            : new("arch", "架构", PreflightState.Fail, "不支持 " + BundleLayout.PlatformKey));

        var binary = BundleLayout.ServeBinaryExists;
        items.Add(new("bin", "程序", binary ? PreflightState.Ok : PreflightState.Fail,
            binary ? "就绪" : "缺少 kiwix-serve"));

        var count = Library().Count;
        items.Add(new("zim", "内容库", count > 0 ? PreflightState.Ok : PreflightState.Warn,
            count > 0 ? $"{count} 本" : "未放入 .zim"));

        var busy = BundleLayout.PortInUse(port);
        items.Add(new("port", "端口", busy ? PreflightState.Warn : PreflightState.Ok,
            busy ? "占用" : $"{port} 可用"));

        var mem = BundleLayout.AvailableMemoryGb();
        items.Add(new("mem", "内存", mem < 0 || mem >= 1.5 ? PreflightState.Ok : PreflightState.Warn,
            mem < 0 ? "未知" : $"{mem:0.0} GB"));

        _preflight.Clear();
        foreach (var i in items) _preflight[i.Key] = i;
        return items;
    }

    public PreflightItem? Check(string key) =>
        _preflight.TryGetValue(key, out var v) ? v : null;

    // ---------------------------------------------------------------- lifecycle

    public async Task<StartResult> StartAsync(int requestedPort, CancellationToken ct = default)
    {
        if (IsRunning) return StartResult.AlreadyRunning;

        var pre = Preflight(requestedPort);
        PreflightItem? Find(string k) => pre.FirstOrDefault(p => p.Key == k);

        if (Find("bin") is { Ok: false }) return StartResult.MissingBinary;
        if (Find("arch") is { Ok: false }) return StartResult.UnsupportedPlatform;

        var zims = BundleLayout.ListZims();
        if (zims.Count == 0) return StartResult.NoLibrary;

        var mem = BundleLayout.AvailableMemoryGb();
        if (mem is > 0 and < 1.5) return StartResult.LowMemory(mem);

        var port = requestedPort;
        if (BundleLayout.PortInUse(port))
        {
            var alt = BundleLayout.FreePort(port);
            // We never terminate whoever owns the original port.
            LogLine?.Invoke($"! 端口 {port} 已被其它程序占用。");
            LogLine?.Invoke($"  本应用没有结束该程序，已改用端口 {alt}。");
            port = alt;
        }

        var exe = BundleLayout.ServeBinary;
        // The guard has to be written as OperatingSystem.IsWindows() rather than
        // BundleLayout.IsWindows: only the former is understood by the
        // platform-compatibility analyzer, so this compiles warning-free.
        if (!OperatingSystem.IsWindows()) MakeExecutable(exe);

        var args = new List<string> { $"--port={port}" };
        args.AddRange(zims.Select(z => Quote(Path.Combine(BundleLayout.ZimDirectory, z.Name))));

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = BundleLayout.ZimDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _proc = null;
            LogLine?.Invoke("启动失败: " + ex.Message);
            return StartResult.LaunchFailed(ex.Message);
        }

        if (_proc is null) return StartResult.LaunchFailed("进程未能创建");

        ActivePort = port;
        var started = _proc;
        started.EnableRaisingEvents = true;
        started.Exited += (_, _) =>
        {
            // Read `started`, not the _proc field: a second start would have
            // overwritten the field, and this handler would then report the exit
            // code of an unrelated process.
            var code = SafeExitCode(started);
            Exited?.Invoke(code);
            if (ReferenceEquals(_proc, started)) Cleanup();
        };

        // Everything the child prints, kept so a startup crash can be explained.
        var captured = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                var err = started.StandardError.ReadToEndAsync(ct);
                while (await started.StandardOutput.ReadLineAsync(ct) is { } line)
                {
                    if (captured.Count > 200) captured.TryDequeue(out _);
                    captured.Enqueue(line);
                    LogLine?.Invoke(line);
                }
                var e = await err;
                if (!string.IsNullOrWhiteSpace(e))
                {
                    foreach (var line in e.Split('\n'))
                    {
                        if (captured.Count > 200) captured.TryDequeue(out _);
                        captured.Enqueue(line);
                    }
                    LogLine?.Invoke(e);
                }
            }
            catch { }
        }, ct);

        // A live process is not a serving server. Reporting success here is what
        // made the first release look broken: kiwix-serve would die a moment
        // later and the window kept claiming everything was fine.
        var deadline = DateTime.UtcNow.AddSeconds(StartupProbeSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (started.HasExited)
            {
                var code = SafeExitCode(started);
                var tail = string.Join('\n', captured.Reverse().Take(6));
                LogLine?.Invoke($"!! 启动后立即退出，返回码 {code}");
                Cleanup();
                return StartResult.Died(code, tail);
            }
            if (BundleLayout.HttpAlive(port, 800)) return StartResult.Ok(port);
            await Task.Delay(300, ct);
        }

        // Still running but not answering yet. A 100 GB+ library can take a
        // while to open, and the caller shows "starting" rather than failing.
        LogLine?.Invoke($"服务进程已启动，{StartupProbeSeconds} 秒内尚未响应，请稍候 ...");
        return StartResult.Ok(port);
    }

    public void Stop()
    {
        if (_proc is null || _proc.HasExited) return;
        LogLine?.Invoke("正在停止服务 ...");
        try
        {
            // kiwix-serve is a console process and never has a main window, so
            // CloseMainWindow is a no-op; go straight for a graceful-ish kill.
            if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true);
        }
        catch { try { _proc.Kill(entireProcessTree: true); } catch { } }
    }

    /// <summary>
    /// The version string the bundled kiwix-serve actually reports.
    ///
    /// It cannot be hardcoded: upstream does not publish every platform on every
    /// release, so for 3.8.2 the Windows build is 3.8.1. The UI used to claim
    /// 3.8.2 on Windows, which was simply wrong. Probed once and cached, and
    /// failure is not fatal -- it is only a label.
    /// </summary>
    public string KiwixToolsVersion
    {
        get
        {
            if (_version is not null) return _version;
            _version = "未知";
            try
            {
                var exe = BundleLayout.ServeBinary;
                if (File.Exists(exe))
                {
                    using var p = Process.Start(new ProcessStartInfo(exe, "--version")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    });
                    var text = p?.StandardOutput.ReadToEnd() ?? "";
                    p?.WaitForExit(8000);
                    var m = Regex.Match(text, @"kiwix-tools\s+([0-9][^\s|]*)");
                    if (m.Success) _version = m.Groups[1].Value;
                }
            }
            catch { }
            return _version;
        }
    }

    private string? _version;

    private void Cleanup()
    {
        _proc?.Dispose();
        _proc = null;
        Stopped?.Invoke();
    }

    private static int SafeExitCode(Process? p)
    {
        try { return p?.ExitCode ?? 0; } catch { return 0; }
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;

    /// <summary>Add the owner execute bit so a ZIM-less copy of the bundle works.</summary>
    /// <remarks>
    /// Unix permission bits have no meaning on Windows, and the USB bundle is
    /// normally authored on Windows, so a ZIM-less copy can arrive without the
    /// bit set. Best effort: a filesystem that refuses the call is not fatal.
    /// </remarks>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void MakeExecutable(string path)
    {
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { }
    }

    // ---------------------------------------------------------------- self check

    /// <summary>
    /// Asks the server how many books it actually loaded. A running process is
    /// not proof that every ZIM was accepted; this is.
    /// </summary>
    public async Task<int> CountLoadedBooksAsync(int port, CancellationToken ct = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        var body = await client.GetStringAsync($"http://127.0.0.1:{port}/catalog/v2/entries", ct);
        return Regex.Matches(body, "<entry>").Count;
    }

    // ---------------------------------------------------------------- integrity

    private static readonly Regex BaselineLine =
        new(@"^([0-9A-Fa-f]{64})\s+\d+\s+.*?([^\/\\]+\.zim)\s*$", RegexOptions.Compiled);

    public IReadOnlyDictionary<string, string> LoadBaseline()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(BundleLayout.LibraryDirectory)) return map;
        foreach (var line in File.ReadAllLines(BundleLayout.LibraryDirectory))
        {
            var m = BaselineLine.Match(line.Trim());
            if (m.Success) map[m.Groups[2].Value] = m.Groups[1].Value.ToUpperInvariant();
        }
        return map;
    }

    /// <summary>Explicit, on-demand only. Never runs at startup.</summary>
    public async Task VerifyAsync(IProgress<(string Name, double Fraction, string Result)> progress,
                                   CancellationToken ct = default)
    {
        var baseline = LoadBaseline();
        foreach (var (name, size) in BundleLayout.ListZims())
        {
            var path = Path.Combine(BundleLayout.ZimDirectory, name);
            progress.Report((name, 0, "计算中"));
            using var sha = System.Security.Cryptography.SHA256.Create();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                bufferSize: 8 * 1024 * 1024, useAsync: true);
            var buffer = new byte[8 * 1024 * 1024];
            long read = 0;
            int n;
            while ((n = await fs.ReadAsync(buffer, ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                progress.Report((name, size > 0 ? (double)read / size : 0, "计算中"));
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var digest = Convert.ToHexString(sha.Hash!);

            if (!baseline.TryGetValue(name, out var expected)) SetIntegrity(name, "无基线", PreflightState.Warn);
            else if (expected == digest) SetIntegrity(name, "校验通过", PreflightState.Ok);
            else SetIntegrity(name, "不匹配", PreflightState.Fail);
        }
    }

    private void SetIntegrity(string name, string text, PreflightState state) =>
        _integrity[name] = (text, state);

    public void Dispose() => Stop();
}

public enum StartResultKind
{
    Ok, AlreadyRunning, MissingBinary, UnsupportedPlatform, NoLibrary, LowMemory,
    LaunchFailed, DiedDuringStartup
}

public readonly record struct StartResult(StartResultKind Kind, int Port = 0, double MemoryGb = 0,
                                          string Message = "", int ExitCode = 0)
{
    public static StartResult Ok(int port) => new(StartResultKind.Ok, port);
    public static StartResult AlreadyRunning => new(StartResultKind.AlreadyRunning);
    public static StartResult MissingBinary => new(StartResultKind.MissingBinary);
    public static StartResult UnsupportedPlatform => new(StartResultKind.UnsupportedPlatform);
    public static StartResult NoLibrary => new(StartResultKind.NoLibrary);
    public static StartResult LowMemory(double gb) => new(StartResultKind.LowMemory, MemoryGb: gb);
    public static StartResult LaunchFailed(string m) => new(StartResultKind.LaunchFailed, Message: m);

    /// <summary>The process came up and then quit before it ever served a request.</summary>
    public static StartResult Died(int exitCode, string output) =>
        new(StartResultKind.DiedDuringStartup, Message: output, ExitCode: exitCode);
}
