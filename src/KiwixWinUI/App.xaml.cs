using System;
using System.Threading;
using Microsoft.UI.Xaml;

namespace KiwixWinUI;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless verification path: KiwixWinUI.exe --selftest
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase))
            {
                SelfTest.EnsureConsole();
                // Must NOT be awaited on the UI thread. The self-test blocks on
                // GetAwaiter().GetResult(), and HttpClient continuations are
                // posted to the current SynchronizationContext -- which is the
                // very UI thread being blocked. That deadlocks. A plain Thread
                // has no SynchronizationContext, so the continuations run on
                // the pool and everything unwinds normally.
                var worker = new Thread(() =>
                {
                    var exitCode = SelfTest.RunAsync().GetAwaiter().GetResult();
                    Environment.Exit(exitCode);
                })
                { IsBackground = false, Name = "kiwix-selftest" };
                worker.Start();
                return;
            }
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
