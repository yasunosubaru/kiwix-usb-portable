using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KiwixApple.Core;

/// <summary>
/// Finds the portable bundle regardless of where the launcher was started from,
/// and answers the environment questions the window has to show up front.
/// Behaviourally identical to <c>src/KiwixWinUI/Core/BundleLayout.cs</c> so the
/// three front-ends stay interchangeable.
/// </summary>
public static class BundleLayout
{
    public const int DefaultPort = 8092;

    /// <summary>Baseline of the on-demand SHA256 check. Absent in most bundles, which is fine.</summary>
    public const string BaselineFileName = "SHA256_baseline.txt";

    private static readonly string[] SupportedPlatforms =
    {
        "windows-x86_64", "linux-x86_64", "linux-aarch64", "linux-armv8", "linux-i586"
    };

    /// <summary>
    /// Walks up from the executable looking for the directory that holds both
    /// zim/ and app/. AppContext.BaseDirectory is the only anchor that is
    /// correct for a self-contained deployment; Environment.CurrentDirectory is
    /// wherever the user happened to double-click from.
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    public static string ZimDirectory => Path.Combine(Root, "zim");
    public static string AppDirectory => Path.Combine(Root, "app");
    public static string IntegrityBaseline => Path.Combine(ZimDirectory, BaselineFileName);

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static string PlatformKey => DetectPlatform();

    public static string ServeBinary =>
        Path.Combine(AppDirectory, PlatformKey, IsWindows ? "kiwix-serve.exe" : "kiwix-serve");

    private static string ResolveRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 6; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "zim")) &&
                Directory.Exists(Path.Combine(dir, "app")))
            {
                return dir;
            }
            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string DetectPlatform()
    {
        if (OperatingSystem.IsWindows()) return "windows-x86_64";
        if (OperatingSystem.IsMacOS()) return "macos-" + (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64");
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.Arm => "armv7",
            Architecture.X86 => "i586",
            _ => "unknown"
        };
        return "linux-" + arch;
    }

    public static bool IsPlatformSupported => SupportedPlatforms.Contains(PlatformKey);

    public static IReadOnlyList<string> SupportedPlatformsList => SupportedPlatforms;

    public static bool ServeBinaryExists => File.Exists(ServeBinary);

    /// <summary>
    /// The ZIM files are the user's property, not ours: only ever opened for
    /// reading, and the UI says so before it verifies a hash.
    /// </summary>
    public static IReadOnlyList<(string Name, long Size)> ListZims()
    {
        if (!Directory.Exists(ZimDirectory)) return Array.Empty<(string, long)>();
        return Directory.EnumerateFiles(ZimDirectory)
            .Where(f => f.EndsWith(".zim", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetFileName(f), new FileInfo(f).Length))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The address to show other devices. Obtained by asking the routing table
    /// which local interface would be used to reach the outside world -- a UDP
    /// Connect with no SendTo and no payload transmits nothing, which is what
    /// keeps this inside the "no network access" promise of the product.
    /// </summary>
    public static string LocalIp()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("10.255.255.255"), 1));
            var addr = probe.LocalEndPoint as IPEndPoint;
            if (addr is not null && !IPAddress.IsLoopback(addr.Address)) return addr.Address.ToString();
        }
        catch { /* no route to anywhere: fall through */ }
        return "127.0.0.1";
    }

    public static bool PortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (SocketException) { return true; }
    }

    /// <summary>Is something actually accepting connections on the loopback port?</summary>
    public static bool HttpAlive(int port, int timeoutMs = 2000)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, port);
            return connect.Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    /// <summary>First free port at or after <paramref name="preferred"/>. Never evicts the incumbent.</summary>
    public static int FreePort(int preferred)
    {
        var p = preferred;
        for (var i = 0; i < 50 && PortInUse(p); i++) p++;
        return p;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static double AvailableMemoryGb()
    {
        // GlobalMemoryStatusEx rather than PerformanceCounter: the counter needs
        // an extra package and only answers after a first sample, and the
        // pre-flight check runs during window construction.
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem)) return mem.ullAvailPhys / 1024d / 1024d / 1024d;
        }
        catch { /* fall through to "unknown" */ }
        return -1;
    }

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(identity)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static string HumanSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.0} {units[i]}";
    }
}
