using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KiwixWinUI;

/// <summary>
/// Locates the portable bundle regardless of where the launcher was started
/// from, and answers the environment questions the UI has to show up front.
/// Mirrors the behaviour of the Python/tkinter launcher on purpose so both
/// front-ends stay interchangeable.
/// </summary>
public static class BundleLayout
{
    public const string KiwixVersion = "3.8.2";
    public const int DefaultPort = 8092;

    private static readonly string[] SupportedPlatforms =
    {
        "windows-x86_64", "linux-x86_64", "linux-aarch64", "linux-armv8", "linux-i586"
    };

    /// <summary>
    /// Walks up from the executable looking for the directory that holds both
    /// zim/ and app/. AppContext.BaseDirectory is the only reliable anchor
    /// for a self-contained deployment.
    /// </summary>
    public static string Root { get; } = ResolveRoot();

    public static string ZimDirectory => Path.Combine(Root, "zim");
    public static string AppDirectory => Path.Combine(Root, "app");
    public static string LibraryDirectory => Path.Combine(ZimDirectory, "SHA256_baseline.txt");

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

    public static IReadOnlyList<(string Name, long Size)> ListZims()
    {
        if (!Directory.Exists(ZimDirectory)) return Array.Empty<(string, long)>();
        return Directory.EnumerateFiles(ZimDirectory)
            .Where(f => f.EndsWith(".zim", StringComparison.OrdinalIgnoreCase))
            .Select(f => (Path.GetFileName(f), new FileInfo(f).Length))
            .OrderBy(x => x.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Local outbound IP without sending a packet.</summary>
    public static string LocalIp()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("10.255.255.255"), 1));
            var addr = probe.LocalEndPoint as IPEndPoint;
            if (addr is not null && !IPAddress.IsLoopback(addr.Address)) return addr.Address.ToString();
        }
        catch { /* fall through */ }
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
        // PerformanceCounter lives in a separate package; the Win32 call keeps
        // the dependency set to just the Windows App SDK.
        try
        {
            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem)) return mem.ullAvailPhys / 1024d / 1024d / 1024d;
        }
        catch { }
        return -1;
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
