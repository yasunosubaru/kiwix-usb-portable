using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KiwixApple.Core;

public enum PreflightState { Ok, Warn, Fail, Unknown }

public sealed record PreflightItem(string Key, string Label, PreflightState State, string Detail)
{
    public bool Ok => State == PreflightState.Ok;
}

public sealed record ZimEntry(string Name, long Size, string Integrity, PreflightState IntegrityState)
{
    public string SizeText => BundleLayout.HumanSize(Size);
}

public enum StartResultKind
{
    Ok, AlreadyRunning, StartInFlight, MissingBinary, UnsupportedPlatform, NoLibrary, LowMemory,
    LaunchFailed, DiedDuringStartup
}

public readonly record struct StartResult(StartResultKind Kind, int Port = 0, double MemoryGb = 0,
                                          string Message = "", int ExitCode = 0)
{
    public static StartResult Ok(int port) => new(StartResultKind.Ok, port);
    public static StartResult AlreadyRunning => new(StartResultKind.AlreadyRunning);
    public static StartResult StartInFlight => new(StartResultKind.StartInFlight);
    public static StartResult MissingBinary => new(StartResultKind.MissingBinary);
    public static StartResult UnsupportedPlatform => new(StartResultKind.UnsupportedPlatform);
    public static StartResult NoLibrary => new(StartResultKind.NoLibrary);
    public static StartResult LowMemory(double gb) => new(StartResultKind.LowMemory, MemoryGb: gb);
    public static StartResult LaunchFailed(string m) => new(StartResultKind.LaunchFailed, Message: m);

    /// <summary>The process came up and then quit before it ever served a request.</summary>
    public static StartResult Died(int exitCode, string output) =>
        new(StartResultKind.DiedDuringStartup, Message: output, ExitCode: exitCode);
}

/// <summary>
/// Owns the kiwix-serve child process. Everything here is deliberately
/// conservative, and that is the product's contract rather than caution for its
/// own sake: never elevate, never kill a foreign process, never create a
/// scheduled task or firewall rule, never write into zim\, and never send a
/// packet anywhere except the loopback port of the child we started ourselves.
/// </summary>
public sealed class KiwixService : IDisposable
{
    private Process? _proc;

    /// <summary>
    /// A child that has exited but whose handle has not been freed yet.
    ///
    /// The Exited event is delivered on a threadpool thread, and disposing the
    /// Process there destroys the one piece of evidence the user needs: the exit
    /// code. StartCoreAsync is very often inside its wait loop at that exact
    /// moment, about to read ExitCode off the same object. So the handler
    /// retires the process and nothing else, and the handle is released later,
    /// by whoever is certain no one is going to ask again.
    /// </summary>
    private volatile Process? _retired;

    /// <summary>Monotonic per-start id, so a handler can tell whose failure it is.</summary>
    private int _generation;

    /// <summary>The start attempt that is reporting its own child's death, or -1.</summary>
    private int _selfReported = -1;

    private int _startInFlight;
    private readonly Dictionary<string, PreflightItem> _preflight = new();
    private readonly Dictionary<string, (string Text, PreflightState State)> _integrity = new();

    /// <summary>How long to wait for the server to answer before giving up on a clean start.</summary>
    private const int StartupProbeSeconds = 25;

    /// <summary>Lines of child output kept so a startup crash can be explained after the fact.</summary>
    private const int CapturedLineLimit = 200;

    public event Action<string>? LogLine;
    public event Action? Stopped;
    public event Action<int>? Exited;

    public bool IsRunning => _proc is { } p && !HasExited(p);

    public int? ProcessId => IsRunning ? _proc!.Id : null;

    /// <summary>
    /// The port the child is actually bound to. Only ever a loopback port: the
    /// window reads it to build the "open library" URL, so letting a value other
    /// than a verified local port leak in here would put a remote host into a
    /// browser on the user's machine.
    /// </summary>
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

    /// <summary>Exactly the five checks the WinUI front-end shows, in the same order.</summary>
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
        // A second press of a start button while the first start is still waiting
        // for the port would launch a second kiwix-serve, and the two would then
        // fight over the same ZIM handles. The flag is interlocked rather than a
        // plain bool so the self-test path (no UI thread) is protected too.
        if (Interlocked.CompareExchange(ref _startInFlight, 1, 0) != 0)
        {
            LogLine?.Invoke("! 已有一个启动流程在进行中，本次请求被忽略。");
            return StartResult.StartInFlight;
        }

        try
        {
            return await StartCoreAsync(requestedPort, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _startInFlight, 0);
        }
    }

    private async Task<StartResult> StartCoreAsync(int requestedPort, CancellationToken ct)
    {
        if (IsRunning) return StartResult.AlreadyRunning;

        // Free the handle of the previous child now: nothing is waiting on it,
        // and its exit code has long since been reported.
        ReleaseRetired();

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
            // We never terminate whoever owns the original port, and we say so
            // out loud rather than silently succeeding on a different one.
            LogLine?.Invoke($"! 端口 {port} 已被其它程序占用。");
            LogLine?.Invoke($"  本应用没有结束该程序，已改用端口 {alt}。");
            port = alt;
        }

        var exe = BundleLayout.ServeBinary;
        var args = new List<string> { $"--port={port}" };
        args.AddRange(zims.Select(z => Path.Combine(BundleLayout.ZimDirectory, z.Name)));

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
        var generation = ++_generation;
        // This start attempt is going to report its own child's death, with the
        // exit code and the captured output, so the Exited handler must keep its
        // hands off and not raise a second, poorer report of the same event.
        _selfReported = generation;

        started.EnableRaisingEvents = true;
        started.Exited += (_, _) =>
        {
            // Read `started`, not the _proc field: a second start would have
            // overwritten the field, and this handler would then report the exit
            // code of an unrelated process.
            var code = SafeExitCode(started);
            // Retire only. Disposing here would race the startup probe for
            // ExitCode and turn a reported crash into a crash of the launcher.
            _retired = started;
            if (generation == _selfReported) return;
            if (ReferenceEquals(_proc, started)) _proc = null;
            Exited?.Invoke(code);
        };

        try
        {
            // Everything the child prints, kept so a startup crash can be explained.
            var captured = new ConcurrentQueue<string>();
            _ = Task.Run(() => PumpOutputAsync(started, captured, ct), CancellationToken.None);

            // A live process is not a serving server. Reporting success here is
            // what made the first release look broken: kiwix-serve would die a
            // moment later and the window kept claiming everything was fine.
            var deadline = DateTime.UtcNow.AddSeconds(StartupProbeSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (HasExited(started))
                {
                    // Still safe to read: nothing has disposed it yet.
                    var code = SafeExitCode(started);
                    var tail = string.Join('\n', captured.Reverse().Take(6));
                    LogLine?.Invoke($"!! 启动后立即退出，返回码 {code}");
                    Cleanup();
                    return StartResult.Died(code, tail);
                }
                if (BundleLayout.HttpAlive(port, 800)) return StartResult.Ok(port);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            // Still running but not answering yet. A 100 GB+ library can take a
            // while to open, and the caller shows "starting" rather than failing.
            LogLine?.Invoke($"服务进程已启动，{StartupProbeSeconds} 秒内尚未响应，请稍候 ...");
            return StartResult.Ok(port);
        }
        finally
        {
            if (_selfReported == generation) _selfReported = -1;
        }
    }

    private async Task PumpOutputAsync(Process started, ConcurrentQueue<string> captured, CancellationToken ct)
    {
        try
        {
            var err = started.StandardError.ReadToEndAsync(ct);
            while (await started.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (captured.Count > CapturedLineLimit) captured.TryDequeue(out _);
                captured.Enqueue(line);
                LogLine?.Invoke(line);
            }
            var e = await err.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(e))
            {
                foreach (var line in e.Split('\n'))
                {
                    if (captured.Count > CapturedLineLimit) captured.TryDequeue(out _);
                    captured.Enqueue(line);
                }
                LogLine?.Invoke(e);
            }
        }
        catch { /* the pipes close when the child dies; nothing to report */ }
    }

    public void Stop()
    {
        if (_proc is null || HasExited(_proc)) return;
        LogLine?.Invoke("正在停止服务 ...");
        try
        {
            // kiwix-serve is a console process and never owns a main window, so
            // CloseMainWindow is a no-op; go straight for a kill, but always of
            // the handle we started ourselves.
            if (!_proc.WaitForExit(3000)) _proc.Kill(entireProcessTree: true);
        }
        catch { try { _proc.Kill(entireProcessTree: true); } catch { /* already gone */ } }

        // Give the Exited handler a moment to retire the child, so the handle
        // that is about to be released is the one it published.
        for (var i = 0; i < 20 && _retired is null; i++) Thread.Sleep(25);
        ReleaseRetired();
    }

    /// <summary>
    /// The version string the bundled kiwix-serve actually reports.
    ///
    /// It cannot be hardcoded: upstream does not publish every platform on every
    /// release, so for a 3.8.2 bundle the Windows build can be 3.8.1. The UI
    /// used to claim the release number, which was simply wrong. Probed once and
    /// cached; failure is not fatal because it is only a label.
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
            catch { /* no binary to ask: "未知" is the honest answer */ }
            return _version;
        }
    }

    private string? _version;

    private void Cleanup()
    {
        var dead = _proc;
        _proc = null;
        ActivePort = 0;
        try { dead?.Dispose(); } catch { /* the handle is going away regardless */ }
        ReleaseRetired();
        Stopped?.Invoke();
    }

    /// <summary>
    /// Frees a child that has exited and been reported on. Never called while a
    /// start attempt could still be reading ExitCode off it.
    /// </summary>
    private void ReleaseRetired()
    {
        var dead = _retired;
        if (dead is null) return;
        _retired = null;
        if (ReferenceEquals(dead, _proc)) return;   // still the current child
        try { dead.Dispose(); } catch { }
    }

    private static int SafeExitCode(Process? p)
    {
        try { return p?.ExitCode ?? 0; } catch { return 0; }
    }

    /// <summary>
    /// HasExited, but it cannot throw.
    ///
    /// The Exited event is raised on a threadpool thread and its handler
    /// disposes the Process, while the startup probe is still asking the very
    /// same question about the very same object. Reading HasExited on a disposed
    /// Process throws InvalidOperationException, and that turns the one failure
    /// the user most needs explained -- a kiwix-serve that quit before it ever
    /// served -- into an unhandled exception that takes the whole window down
    /// without a word. A disposed process has certainly exited, so that is the
    /// answer to give.
    /// </summary>
    private static bool HasExited(Process? p)
    {
        if (p is null) return true;
        try { return p.HasExited; }
        catch (InvalidOperationException) { return true; }
        catch (SystemException) { return true; }
    }

    // ---------------------------------------------------------------- self check

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>
    /// Hard boundary of the product: the only host this app ever talks to is the
    /// loopback address of the kiwix-serve child we started ourselves. Asserted
    /// rather than assumed, so a future edit that points the catalog probe at a
    /// LAN address fails loudly instead of quietly phoning home.
    /// </summary>
    private static void AssertLoopback(Uri uri)
    {
        if (!IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException($"拒绝非本机请求: {uri}");
    }

    /// <summary>
    /// Asks the server how many books it actually loaded. A running process is
    /// not proof that every ZIM was accepted; this is.
    /// </summary>
    public async Task<int> CountLoadedBooksAsync(int port, CancellationToken ct = default)
    {
        var uri = new Uri($"http://127.0.0.1:{port}/catalog/v2/entries");
        AssertLoopback(uri);
        var body = await Http.GetStringAsync(uri, ct).ConfigureAwait(false);
        return Regex.Matches(body, "<entry>").Count;
    }

    // ---------------------------------------------------------------- integrity

    private static readonly Regex BaselineLine =
        new(@"^([0-9A-Fa-f]{64})\s+\d+\s+.*?([^\/\\]+\.zim)\s*$", RegexOptions.Compiled);

    public IReadOnlyDictionary<string, string> LoadBaseline()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(BundleLayout.IntegrityBaseline)) return map;
        foreach (var line in File.ReadAllLines(BundleLayout.IntegrityBaseline))
        {
            var m = BaselineLine.Match(line.Trim());
            if (m.Success) map[m.Groups[2].Value] = m.Groups[1].Value.ToUpperInvariant();
        }
        return map;
    }

    /// <summary>
    /// Explicit, on-demand only -- never at startup. Hashing 150 GB off a USB
    /// stick takes minutes, and a product that did it silently would look hung
    /// on every launch.
    /// </summary>
    public async Task VerifyAsync(IProgress<(string Name, double Fraction, string Result)> progress,
                                   CancellationToken ct = default)
    {
        var baseline = LoadBaseline();
        foreach (var (name, size) in BundleLayout.ListZims())
        {
            var path = Path.Combine(BundleLayout.ZimDirectory, name);
            progress.Report((name, 0, "计算中"));
            // FileShare.Read + FileAccess.Read: the ZIM files are the user's
            // data and are treated as strictly read-only. Nothing here can open
            // one for writing, so a verify can never damage the library.
            using var sha = SHA256.Create();
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                bufferSize: 8 * 1024 * 1024, useAsync: true);
            var buffer = new byte[8 * 1024 * 1024];
            long read = 0;
            int n;
            while ((n = await fs.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
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

    public void Dispose()
    {
        Stop();
        _proc = null;
        ReleaseRetired();
    }
}
