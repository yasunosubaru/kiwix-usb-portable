using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KiwixApple.Core;

/// <summary>
/// Headless verification of the same service code the window drives, so the
/// launcher can be proven in CI and in a script instead of by eyeballing a
/// window. Exit code 0 = pass, 1 = fail.
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var gate = new object();
        int failures = 0;

        void Line(string s = "") { lock (gate) output.WriteLine(s); }
        void Check(bool ok, string what)
        {
            lock (gate)
            {
                output.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + what);
                if (!ok) failures++;
            }
        }

        Line("=== KiwixApple self-test ===");
        Line($"  bundle root : {BundleLayout.Root}");
        Line($"  platform    : {BundleLayout.PlatformKey} (supported={BundleLayout.IsPlatformSupported})");
        Line($"  zim dir     : {BundleLayout.ZimDirectory}");
        Line($"  serve bin   : {BundleLayout.ServeBinary}");
        Line($"  local ip    : {BundleLayout.LocalIp()}");
        Line();

        Check(BundleLayout.IsPlatformSupported, "platform supported");
        Check(BundleLayout.ServeBinaryExists, "kiwix-serve binary present");

        var zims = BundleLayout.ListZims();
        Check(zims.Count > 0, $"offline library not empty ({zims.Count} zim files)");
        foreach (var (name, size) in zims)
            Line($"          {name}  {BundleLayout.HumanSize(size)}");

        if (zims.Count == 0)
        {
            Line();
            Line("RESULT: FAIL (no library, cannot exercise the service)");
            return 1;
        }

        using var service = new KiwixService();
        service.LogLine += l => Line("  | " + l);

        var port = BundleLayout.FreePort(18999);
        Line();
        Line($"=== start service on port {port} ===");

        var started = await service.StartAsync(port).ConfigureAwait(false);
        if (started.Kind != StartResultKind.Ok)
        {
            Line($"  start failed: {started.Kind} {started.Message}");
            Line();
            Line("RESULT: FAIL (service did not start)");
            return 1;
        }
        Check(started.Port > 0, $"service started on port {started.Port}");
        Check(BundleLayout.HttpAlive(started.Port), "port accepts a loopback connection");

        // Give the index a moment, then ask the server what it actually loaded.
        int loaded = -1;
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000).ConfigureAwait(false);
            try { loaded = await service.CountLoadedBooksAsync(started.Port).ConfigureAwait(false); if (loaded > 0) break; }
            catch { /* not up yet */ }
        }
        Check(loaded == zims.Count, $"server loaded {loaded}/{zims.Count} zim files");

        service.Stop();
        for (var i = 0; i < 20 && service.IsRunning; i++) await Task.Delay(250).ConfigureAwait(false);
        Check(!service.IsRunning, "service stopped cleanly");

        Line();
        Line(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL ({failures})");
        output.Flush();
        return failures == 0 ? 0 : 1;
    }
}
