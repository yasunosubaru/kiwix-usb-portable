using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace KiwixWinUI;

/// <summary>
/// Headless verification, so the launcher can be proven to work in CI and in
/// a script instead of by eyeballing a window.
/// Exit code 0 = pass, 1 = fail.
/// </summary>
public static class SelfTest
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    /// <summary>
    /// This is a WinExe, so stdout goes nowhere unless we borrow the console
    /// that launched us. Without this the self-test prints nothing at all.
    /// </summary>
    public static void EnsureConsole()
    {
        try { AttachConsole(-1); } catch { }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
    }

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
            if (!ok) failures++;
        }

        Console.WriteLine("=== KiwixWinUI self-test ===");
        Console.WriteLine($"  bundle root : {BundleLayout.Root}");
        Console.WriteLine($"  platform    : {BundleLayout.PlatformKey} (supported={BundleLayout.IsPlatformSupported})");
        Console.WriteLine($"  zim dir     : {BundleLayout.ZimDirectory}");
        Console.WriteLine($"  local ip    : {BundleLayout.LocalIp()}");
        Console.WriteLine();

        Check(BundleLayout.IsPlatformSupported, "platform supported");
        Check(BundleLayout.ServeBinaryExists, "kiwix-serve binary present");

        var zims = BundleLayout.ListZims();
        Check(zims.Count > 0, $"offline library not empty ({zims.Count} zim files)");
        foreach (var (name, size) in zims)
            Console.WriteLine($"          {name}  {BundleLayout.HumanSize(size)}");

        if (zims.Count == 0)
        {
            Console.WriteLine("\nRESULT: FAIL (no library, cannot exercise the service)");
            return 1;
        }

        using var service = new KiwixService();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        service.LogLine += l => { log.Enqueue(l); Console.WriteLine("  | " + l); };

        var port = BundleLayout.FreePort(18999);
        Console.WriteLine();
        Console.WriteLine($"=== start service on port {port} ===");
        var started = await service.StartAsync(port);
        if (started.Kind != StartResultKind.Ok)
        {
            Console.WriteLine($"  start failed: {started.Kind} {started.Message}");
            return 1;
        }
        Check(started.Port > 0, $"service started on port {started.Port}");

        // give the index a moment, then ask the server what it actually loaded
        int loaded = -1;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000);
            try { loaded = await service.CountLoadedBooksAsync(started.Port); if (loaded > 0) break; }
            catch { }
        }
        Check(loaded == zims.Count, $"server loaded {loaded}/{zims.Count} zim files");

        service.Stop();
        await Task.Delay(1500);
        Check(!service.IsRunning, "service stopped cleanly");

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        return failures == 0 ? 0 : 1;
    }
}
