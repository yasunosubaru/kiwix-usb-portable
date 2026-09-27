using System;
using System.Linq;
using System.Threading;
using Avalonia;
using KiwixApple.Core;

namespace KiwixApple;

/// <summary>
/// Entry point. Two paths only: a normal Avalonia desktop app, and the headless
/// <c>--selftest</c> verification that proves the same service code works
/// without a display.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var output = ConsoleBridge.OpenOutput();

            // The self-test owns a plain Thread rather than running on the
            // calling one. Its body blocks on GetAwaiter().GetResult() and the
            // continuation of every awaited HttpClient call is posted back to
            // the captured SynchronizationContext: if that context were the UI
            // thread -- or anything else that is not pumping messages while we
            // wait -- the process would deadlock instead of printing a verdict.
            // A bare Thread has no SynchronizationContext at all, so the
            // continuations fall to the thread pool and the call unwinds.
            var worker = new Thread(() => Environment.Exit(SelfTest.RunAsync(output).GetAwaiter().GetResult()))
            {
                IsBackground = false,
                Name = "kiwix-selftest"
            };
            worker.Start();
            worker.Join();
            return 0;   // unreachable: the worker already called Environment.Exit
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>No explicit font package: the type ramp asks for Segoe UI, which every Windows install has.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                   .UsePlatformDetect()
                   .LogToTrace();
}
