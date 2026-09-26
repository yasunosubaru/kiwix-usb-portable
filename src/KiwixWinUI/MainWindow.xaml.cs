using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KiwixWinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace KiwixWinUI;

public sealed partial class MainWindow : Window
{
    private readonly KiwixService _service = new();
    private readonly DispatcherQueueTimer _healthTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private CancellationTokenSource? _verifyCts;
    private DateTime? _startedAt;
    private bool _healthOk;
    private bool _starting;

    /// <summary>
    /// Launcher version, read from the assembly rather than hardcoded here.
    /// A second copy of this string in the code-behind is how the window ended
    /// up claiming v1.1.2 inside a v1.2.0 bundle.
    /// </summary>
    private static string AppVersion
    {
        get
        {
            var asm = typeof(MainWindow).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var v = info?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(v)) v = asm.GetName().Version?.ToString();
            if (string.IsNullOrWhiteSpace(v)) return "unknown";
            // Strip any build metadata: 1.2.1+abcdef -> 1.2.1
            var plus = v.IndexOfAny(new[] { '+', '-' });
            return plus > 0 ? v[..plus] : v;
        }
    }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(RootGrid);
        AppWindow.Resize(new SizeInt32(1080, 820));

        _service.LogLine += line => DispatcherQueue.TryEnqueue(() => AppendLog(line));
        _service.Exited += code => DispatcherQueue.TryEnqueue(() => OnExited(code));

        _healthTimer = DispatcherQueue.CreateTimer();
        _healthTimer.Interval = TimeSpan.FromSeconds(3);
        _healthTimer.Tick += (_, _) => TickHealth();
        _healthTimer.Start();

        _clockTimer = DispatcherQueue.CreateTimer();
        _clockTimer.Interval = TimeSpan.FromSeconds(1);
        _clockTimer.Tick += (_, _) => TickClock();
        _clockTimer.Start();

        AppendLog($"整合包目录: {BundleLayout.Root}");
        AppendLog($"运行平台  : {Environment.OSVersion} / {BundleLayout.PlatformKey}");
        AppendLog("提示      : 纯 U 盘模式需要保持 U 盘插着；若被系统以 noexec 挂载，");
        AppendLog("            请用「飞牛 fnOS 常驻部署」把 ZIM 复制到硬盘后用 Docker 运行。");

        RefreshLibrary();
        RefreshPreflight();
        // Ask the bundled binary instead of trusting a hardcoded string: the
        // Windows build is 3.8.1 while the release is named 3.8.2.
        VersionText.Text = $"kiwix-tools {_service.KiwixToolsVersion} · 本启动器 v{AppVersion}";
        Closed += OnClosed;
    }

    private void RefreshLibrary()
    {
        var items = _service.Library();
        LibraryList.ItemsSource = items;
        LibrarySummary.Text = items.Count > 0
            ? $"共 {items.Count} 本 · 合计 {BundleLayout.HumanSize(_service.LibraryTotalBytes)}"
            : "zim 目录为空";
    }

    private int RequestedPort =>
        int.TryParse(PortBox.Text.Trim(), out var p) ? p : BundleLayout.DefaultPort;

    private void RefreshPreflight()
    {
        PreflightPanel.Children.Clear();
        foreach (var item in _service.Preflight(RequestedPort))
        {
            var (bg, fg) = item.State switch
            {
                PreflightState.Ok => ("#12351F", "#3DDC84"),
                PreflightState.Warn => ("#3A2E12", "#F5A623"),
                PreflightState.Fail => ("#3A1F1F", "#FF5C5C"),
                _ => ("#232838", "#8B93A7")
            };
            PreflightPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Ui.Hex(bg)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = $"{item.Label} {item.Detail}",
                    Foreground = new SolidColorBrush(Ui.Hex(fg)),
                    FontSize = 12
                }
            });
        }
    }

    private void AppendLog(string line)
    {
        LogBox.Text += line + "\n";
    }

    private void SetStatus(string text, string colorHex = "#8B93A7")
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(Ui.Hex(colorHex));
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string detail)
    {
        Notice.Severity = severity;
        Notice.Title = title;
        Notice.Message = detail;
        Notice.IsOpen = true;
    }

    // ------------------------------------------------------------------ events

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_starting)
        {
            SetStatus("正在启动，请稍候 ...", "#F5A623");
            return;
        }
        if (_service.IsRunning) { SetStatus("服务已在运行", "#F5A623"); return; }

        _starting = true;
        StartButton.IsEnabled = false;
        SetStatus("正在启动 kiwix-serve ...", "#4C8DFF");
        try
        {
            await StartCore();
        }
        finally
        {
            _starting = false;
            // Only re-enable if we did not end up running; OnRunning owns the
            // button state once the service is up.
            if (!_service.IsRunning) StartButton.IsEnabled = true;
        }
    }

    private async Task StartCore()
    {
        // Captured before OnRunning rewrites PortBox, otherwise the "did the port
        // move?" comparison below would always be false.
        var wanted = RequestedPort;
        var result = await _service.StartAsync(wanted);
        switch (result.Kind)
        {
            case StartResultKind.MissingBinary:
                ShowNotice(InfoBarSeverity.Error, "无法启动",
                    $"缺少 kiwix-serve 程序：\n{BundleLayout.ServeBinary}\n\n请确认整合包 app 目录完整，或安全软件未将其隔离。");
                return;
            case StartResultKind.UnsupportedPlatform:
                ShowNotice(InfoBarSeverity.Error, "架构不匹配",
                    $"此版本只包含 {string.Join("、", BundleLayout.SupportedPlatformsList)} 的程序。\n当前平台：{BundleLayout.PlatformKey}");
                return;
            case StartResultKind.NoLibrary:
                ShowNotice(InfoBarSeverity.Warning, "没有离线内容",
                    "zim 目录里没有 .zim 文件，无法启动。\n\n请确认 U 盘已插入、资料库目录未被移动。\n" + BundleLayout.ZimDirectory);
                return;
            case StartResultKind.LowMemory:
            {
                var dialog = new ContentDialog
                {
                    Title = "可用内存偏少",
                    Content = $"可用内存仅 {result.MemoryGb:0.0} GB。\n\n118GB 的英文维基全文搜索较吃内存，可能启动失败或被系统回收。\n\n仍要继续吗？",
                    PrimaryButtonText = "继续",
                    CloseButtonText = "取消",
                    XamlRoot = RootGrid.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                result = await _service.StartAsync(RequestedPort);
                break;
            }
            case StartResultKind.LaunchFailed:
                ShowNotice(InfoBarSeverity.Error, "启动失败",
                    result.Message + "\n\n应用没有修改任何文件或系统设置。");
                return;
            case StartResultKind.DiedDuringStartup:
                // The single most confusing failure there is: the process came up
                // and quit before serving anything. Say so plainly, and show what
                // kiwix-serve printed, because the exit code alone means nothing.
                ShowNotice(InfoBarSeverity.Error, "服务启动后立即退出",
                    $"kiwix-serve 启动了，但在开始提供服务前就退出了（返回码 {result.ExitCode}）。\n\n"
                  + "它的输出：\n" + (string.IsNullOrWhiteSpace(result.Message) ? "（无输出）" : result.Message)
                  + "\n\n常见原因：\n· 杀毒软件拦截了 kiwix-serve.exe\n· 资料库文件不完整\n· 系统资源不足\n\n应用不会自动重试。");
                SetStatus("启动失败，详见上方提示", "#FF5C5C");
                return;
        }

        if (result.Kind != StartResultKind.Ok) return;

        // This is the step that flips the UI into the running state: address,
        // badge, button enablement and the uptime clock. Without it the service
        // comes up invisibly, "open library" stays disabled, and the window
        // looks broken while kiwix-serve is happily serving pages.
        OnRunning(result.Port);

        if (result.Port != wanted)
        {
            ShowNotice(InfoBarSeverity.Warning, "端口已顺延",
                $"端口 {wanted} 已被其它程序占用，本应用没有结束它，已自动改用 {result.Port}。\n\n"
              + $"若 {wanted} 上是上次运行残留的服务，可先点「停止」再重新启动。");
        }
        await SelfCheckAsync(result.Port);
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => _service.Stop();

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        if (_service.ActivePort == 0 || !BundleLayout.HttpAlive(_service.ActivePort)) return;

        // Open loopback, not the LAN address shown above. The browser is on this
        // machine, and Windows Firewall blocks inbound connections by default,
        // so http://<lan-ip>:<port>/ can be unreachable from the very machine
        // serving it. Loopback is never firewalled.
        //
        // kiwix's index script also auto-applies a language filter derived from
        // the browser UI language when the URL carries no fragment, and persists
        // it in a cookie. Appending an empty filter skips that entirely.
        OpenUrl($"http://127.0.0.1:{_service.ActivePort}#lang=");
    }

    private void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(UrlText.Text);
        Clipboard.SetContent(pkg);
        SetStatus("已复制: " + UrlText.Text, "#3DDC84");
    }

    private void OnCopyFnOsClick(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText("sudo sh \"$(pwd)/install-fnos.sh\"");
        Clipboard.SetContent(pkg);
        SetStatus("已复制安装命令", "#3DDC84");
    }

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        if (_service.Library().Count == 0) return;
        _verifyCts?.Cancel();
        _verifyCts = new CancellationTokenSource();

        VerifyButton.IsEnabled = false;
        VerifyProgress.Visibility = Visibility.Visible;
        VerifyProgress.Value = 0;
        SetStatus("正在校验 SHA256，请勿拔出存储设备 ...", "#4C8DFF");

        var progress = new Progress<(string Name, double Fraction, string Result)>(p =>
        {
            if (p.Fraction > 0) VerifyProgress.Value = Math.Clamp(p.Fraction * 100, 0, 100);
        });

        try
        {
            await _service.VerifyAsync(progress, _verifyCts.Token);
            SetStatus("校验完成", "#3DDC84");
        }
        catch (OperationCanceledException)
        {
            SetStatus("校验已取消", "#F5A623");
        }
        finally
        {
            VerifyButton.IsEnabled = true;
            VerifyProgress.Visibility = Visibility.Collapsed;
            RefreshLibrary();
        }
    }

    // ------------------------------------------------------------------ state

    private void OnRunning(int port)
    {
        _startedAt = DateTime.Now;
        _healthOk = false;
        // The LAN address is what other devices need, so that is what we show.
        // Opening the browser goes through loopback instead -- see OnOpenClick.
        UrlText.Text = $"http://{BundleLayout.LocalIp()}:{port}";
        PortBox.Text = port.ToString();
        BadgeText.Text = "运行中";
        BadgeBorder.Background = new SolidColorBrush(Ui.Hex("#12351F"));
        BadgeText.Foreground = new SolidColorBrush(Ui.Hex("#3DDC84"));
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenButton.IsEnabled = true;
        PortBox.IsEnabled = false;
        Notice.IsOpen = false;
        SetStatus($"运行中 · 本机请用 http://127.0.0.1:{port} 打开，其他设备用上方地址", "#3DDC84");
    }

    private void OnExited(int code)
    {
        _startedAt = null;
        _healthOk = false;
        UrlText.Text = "—";
        BadgeText.Text = "未运行";
        BadgeBorder.Background = new SolidColorBrush(Ui.Hex("#232838"));
        BadgeText.Foreground = new SolidColorBrush(Ui.Hex("#8B93A7"));
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        PortBox.IsEnabled = true;
        RuntimeText.Text = "";

        if (code is 0 or -15)
        {
            SetStatus("服务已停止");
            return;
        }

        AppendLog($"!! 进程异常退出，返回码 {code}");
        SetStatus("服务未能持续运行，请查看日志", "#FF5C5C");
        ShowNotice(InfoBarSeverity.Error, "服务已停止",
            $"kiwix-serve 未能持续运行（退出码 {code}）。\n\n常见原因：\n· 端口被占用\n· 资料库不完整或版本不兼容\n· 存储设备被系统以 noexec 挂载\n\n应用不会自动重试，也不会修改你的文件。");
    }

    private void TickHealth()
    {
        if (!_service.IsRunning || _service.ActivePort == 0) return;
        var ok = BundleLayout.HttpAlive(_service.ActivePort);
        if (ok && !_healthOk) AppendLog("本机健康检查通过：HTTP 服务已响应");
        _healthOk = ok;
    }

    private void TickClock()
    {
        if (_service.IsRunning && _startedAt is { } start)
        {
            var span = DateTime.Now - start;
            RuntimeText.Text = $"PID {_service.ProcessId} · 已运行 {(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}"
                              + (_healthOk ? "" : " · 无响应");
        }
        else
        {
            RuntimeText.Text = "";
        }
    }

    /// <summary>
    /// A live process is not proof that every ZIM was accepted, so ask the
    /// server how many books it actually loaded and compare with disk.
    /// </summary>
    private async Task SelfCheckAsync(int port)
    {
        int loaded;
        try { loaded = await _service.CountLoadedBooksAsync(port); }
        catch (Exception ex)
        {
            AppendLog("! 自检失败，无法读取书目: " + ex.Message);
            return;
        }

        var expected = _service.Library().Count;
        if (loaded == expected)
        {
            AppendLog($"自检通过：服务器已加载 {loaded}/{expected} 本资料库");
            SetStatus($"已加载 {loaded}/{expected} 本 · 本机已响应（外网/其他设备可达性取决于防火墙）", "#3DDC84");
        }
        else
        {
            AppendLog($"! 自检异常：磁盘上有 {expected} 本，服务器只加载了 {loaded} 本");
            SetStatus($"只加载了 {loaded}/{expected} 本，请查看日志", "#FF5C5C");
            ShowNotice(InfoBarSeverity.Warning, "资料库未被全部加载",
                $"磁盘上有 {expected} 个 .zim，但服务器只加载了 {loaded} 个。\n\n可能是某个文件不完整或被截断。应用不会修改你的文件。");
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* nothing sensible to do */ }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        _verifyCts?.Cancel();
        _healthTimer.Stop();
        _clockTimer.Stop();

        if (_service.IsRunning)
        {
            var dialog = new ContentDialog
            {
                Title = "退出",
                Content = "服务还在运行。\n\n关闭本窗口会一并停止它，确定吗？",
                PrimaryButtonText = "退出并停止",
                CloseButtonText = "取消",
                XamlRoot = RootGrid.XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                args.Handled = true;   // veto the close
                return;
            }
        }
        _service.Dispose();
    }
}
