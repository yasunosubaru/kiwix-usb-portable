using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KiwixApple.Core;

// Avalonia's layout enums are also instance properties of Layoutable, so inside
// a Window the bare names resolve to the instance and cannot be used as
// values. Aliases keep the object initialisers readable.
using HAlign = Avalonia.Layout.HorizontalAlignment;
using VAlign = Avalonia.Layout.VerticalAlignment;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace KiwixApple.Views;

public partial class MainWindow : Window
{
    private readonly KiwixService _service = new();
    private readonly AppleAlert _alert = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _verifyCts;
    private CancellationTokenSource? _healthCts;
    private DateTime? _startedAt;
    private int? _activePort;
    private bool _healthOk;
    private bool _starting;
    private bool _closeConfirmed;
    private bool _closePromptOpen;

    /// <summary>
    /// Launcher version, read off the assembly instead of hardcoded here. A
    /// second copy of this string in the code-behind is how the WinUI front-end
    /// ended up claiming v1.1.2 inside a v1.2.0 bundle.
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
            // Strip build metadata: 1.2.1+abcdef -> 1.2.1
            var plus = v.IndexOfAny(new[] { '+', '-' });
            return plus > 0 ? v[..plus] : v;
        }
    }

    private const int MaxLogChars = 64 * 1024;

    public MainWindow()
    {
        InitializeComponent();

        // The alert sheet lives in the window's own overlay grid, so it is
        // above the scroll content and above the navigation bar by construction.
        AlertLayer.Children.Add(_alert);
        AlertLayer.IsVisible = false;

        _service.LogLine += line => Dispatcher.UIThread.Post(() => AppendLog(line));
        _service.Exited += code => Dispatcher.UIThread.Post(() => OnExited(code));

        ContentScroll.ScrollChanged += (_, _) => UpdateNavChrome();
        _clock.Tick += (_, _) => TickClock();
        _clock.Start();

        StartButton.Click += OnStartClick;
        StopButton.Click += (_, _) => _service.Stop();
        OpenButton.Click += OnOpenClick;
        CopyUrlButton.Click += OnCopyUrlClick;
        VerifyButton.Click += OnVerifyClick;
        PortBox.TextChanged += OnPortTextChanged;
        Closing += OnClosing;

        AppendLog($"整合包目录: {BundleLayout.Root}");
        AppendLog($"运行平台  : {Environment.OSVersion} / {BundleLayout.PlatformKey}");
        if (BundleLayout.IsElevated)
        {
            // Never ask for elevation ourselves, and say so plainly if the user
            // launched the window as administrator: kiwix-serve does not need
            // it, and an elevated parent would let the child write to zim\.
            AppendLog("注意      : 本窗口以管理员身份运行。本应用从不自行提权。");
            SetStatus("以管理员身份运行。kiwix-serve 无需提权，建议普通用户启动。", "warn");
        }
        AppendLog("提示      : 内容库只读，本应用不会写入 zim\\ 目录。");

        RefreshLibrary();
        RefreshPreflight();
        if (_statusUntouched) SetStatus("就绪", "idle");

        // The uppercase half of the grouped-table section header style. Avalonia
        // 11 has no TextTransform on TextBlock, so the transform lives here
        // instead of in the theme. Chinese has no case and is unaffected, which
        // is the correct outcome: HIG localises the header, it does not shout
        // it, and the letter-spacing is what carries the style.
        UpperCaseHeaders(HeaderService, HeaderPreflight, HeaderLibrary, HeaderLog);

        // Ask the bundled binary instead of trusting a hardcoded string: the
        // Windows build of a 3.8.2 release is often 3.8.1.
        VersionText.Text = $"kiwix-tools {_service.KiwixToolsVersion} · 启动器 v{AppVersion}";

        UpdateNavChrome();
    }

    private static void UpperCaseHeaders(params TextBlock[] headers)
    {
        foreach (var h in headers)
        {
            if (h.Text is { Length: > 0 } t) h.Text = t.ToUpperInvariant();
        }
    }

    private bool _statusUntouched = true;

    // ------------------------------------------------------------------ layout

    /// <summary>
    /// UIKit's large-title collapse. The big title lives inside the scroll
    /// content and leaves on its own; what has to be animated is the small
    /// inline title, the backdrop and the hairline that replace it.
    /// </summary>
    private void UpdateNavChrome()
    {
        // 28pt of travel: roughly the descender of the 34pt large title, which
        // is the point at which UIKit has the small title fully in place.
        var t = Math.Clamp(ContentScroll.Offset.Y / 28.0, 0, 1);
        InlineTitle.Opacity = t;
        NavBackdrop.Opacity = t;
        NavHairline.Opacity = t;
    }

    // ------------------------------------------------------------------ content

    private void RefreshLibrary()
    {
        var items = _service.Library();
        LibraryPanel.Children.Clear();
        LibrarySummary.Text = items.Count > 0
            ? $"共 {items.Count} 本 · 合计 {BundleLayout.HumanSize(_service.LibraryTotalBytes)}"
            : "zim 目录为空";

        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) LibraryPanel.Children.Add(new Border { Classes = { "separator" } });
            LibraryPanel.Children.Add(LibraryRow(items[i]));
        }

        VerifyButton.IsEnabled = items.Count > 0;
    }

    private static Grid LibraryRow(ZimEntry item)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 44
        };
        AutomationProperties.SetAutomationId(row, "Library_" + item.Name);

        row.Children.Add(new TextBlock
        {
            Text = item.Name,
            VerticalAlignment = VAlign.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var state = item.IntegrityState switch
        {
            PreflightState.Ok => "ok",
            PreflightState.Warn => "warn",
            PreflightState.Fail => "fail",
            _ => string.Empty
        };
        var value = new TextBlock
        {
            Classes = { "rowValue" },
            Text = $"{item.SizeText} · {item.Integrity}",
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        if (state.Length > 0) value.Classes.Add(state);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private void RefreshPreflight()
    {
        var items = _service.Preflight(RequestedPort);
        PreflightPanel.Children.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0) PreflightPanel.Children.Add(new Border { Classes = { "separator" } });
            PreflightPanel.Children.Add(PreflightRow(items[i]));
        }
    }

    private static Grid PreflightRow(PreflightItem item)
    {
        // Status marks are stroked paths, not glyphs: U+2713 and its relatives
        // are missing from plenty of font stacks and render as tofu, and a
        // vector stays crisp at 200% scaling.
        var (geometry, state) = item.State switch
        {
            PreflightState.Ok => ("M 1.5,7 L 5,10.5 L 11.5,2.5", "ok"),
            PreflightState.Warn => ("M 6.5,2 L 6.5,7.5 M 6.5,10.4 L 6.5,10.5", "warn"),
            PreflightState.Fail => ("M 2.5,2.5 L 10.5,10.5 M 10.5,2.5 L 2.5,10.5", "fail"),
            _ => ("M 2,6.5 L 11,6.5", "unknown")
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("24,Auto,*"),
            MinHeight = 44
        };
        AutomationProperties.SetAutomationId(row, "Preflight_" + item.Key);

        row.Children.Add(new ShapePath
        {
            Classes = { "statusMark", state },
            Data = Geometry.Parse(geometry)
        });

        var label = new TextBlock
        {
            Text = item.Label,
            VerticalAlignment = VAlign.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        var value = new TextBlock
        {
            Classes = { "rowValue" },
            Text = item.Detail,
            HorizontalAlignment = HAlign.Right,
            VerticalAlignment = VAlign.Center,
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        if (item.State == PreflightState.Warn) value.Classes.Add("warn");
        else if (item.State == PreflightState.Fail) value.Classes.Add("fail");
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    private void AppendLog(string line)
    {
        _log.AppendLine(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line);
        // Bounded: a chatty kiwix-serve must not grow the log without limit on a
        // 2 GB stick.
        if (_log.Length > MaxLogChars)
        {
            // Drop the oldest half rather than the oldest line: cheap, and the
            // log is a console transcript, not a file.
            var cut = _log.ToString();
            var keep = cut.Length / 2;
            _log.Clear();
            _log.Append(cut, keep, cut.Length - keep);
        }
        LogBox.Text = _log.ToString();
        LogBox.CaretIndex = LogBox.Text.Length;
    }

    private void SetStatus(string text, string state = "Idle")
    {
        StatusText.Text = text;
        StatusText.Classes.Set("statusIdle", false);
        StatusText.Classes.Set("statusBusy", false);
        StatusText.Classes.Set("statusOk", false);
        StatusText.Classes.Set("statusWarn", false);
        StatusText.Classes.Set("statusError", false);
        StatusText.Classes.Add("status" + state);
        if (state != "busy") _statusUntouched = false;
    }

    private void SetBadge(string text, string state)
    {
        BadgeText.Text = text;
        foreach (var s in new[] { "Idle", "Ok", "Busy" })
        {
            BadgeBorder.Classes.Set("badge" + s, false);
            BadgeText.Classes.Set("badge" + s, false);
        }
        BadgeBorder.Classes.Add("badge" + state);
        BadgeText.Classes.Add("badge" + state);
    }

    private int RequestedPort =>
        int.TryParse(PortBox.Text?.Trim(), out var p) ? p : BundleLayout.DefaultPort;

    private void OnPortTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (PortBox.IsEnabled) RefreshPreflight();
    }

    // ------------------------------------------------------------------ actions

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        // Belt and braces: KiwixService also guards with an interlocked flag,
        // because a double-click can produce two Click events before the first
        // handler has reached its await.
        if (_starting)
        {
            SetStatus("正在启动，请稍候 ...", "busy");
            return;
        }
        if (_service.IsRunning)
        {
            SetStatus("服务已在运行", "warn");
            return;
        }

        _starting = true;
        StartButton.IsEnabled = false;
        SetStatus("正在启动 kiwix-serve ...", "busy");
        SetBadge("启动中", "Busy");
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _starting = false;
            // Only re-enable if we did not end up running; OnRunning owns the
            // button state once the service is up.
            if (!_service.IsRunning) StartButton.IsEnabled = true;
        }
    }

    private async Task StartCoreAsync()
    {
        // Captured before OnRunning rewrites PortBox, otherwise the "did the
        // port move?" comparison below would always be false.
        var wanted = RequestedPort;
        var result = await _service.StartAsync(wanted);

        switch (result.Kind)
        {
            case StartResultKind.StartInFlight:
                SetStatus("已有一个启动流程在进行中 ...", "busy");
                return;
            case StartResultKind.MissingBinary:
                SetStatus("缺少 kiwix-serve 程序", "error");
                await NotifyAsync(AlertKind.Error, "无法启动",
                    $"缺少 kiwix-serve 程序：\n{BundleLayout.ServeBinary}\n\n请确认整合包 app 目录完整，或安全软件未将其隔离。");
                return;
            case StartResultKind.UnsupportedPlatform:
                SetStatus("架构不匹配", "error");
                await NotifyAsync(AlertKind.Error, "架构不匹配",
                    $"此版本只包含 {string.Join("、", BundleLayout.SupportedPlatformsList)} 的程序。\n当前平台：{BundleLayout.PlatformKey}");
                return;
            case StartResultKind.NoLibrary:
                SetStatus("没有离线内容", "warn");
                await NotifyAsync(AlertKind.Warning, "没有离线内容",
                    "zim 目录里没有 .zim 文件，无法启动。\n\n请确认 U 盘已插入、资料库目录未被移动。\n" + BundleLayout.ZimDirectory);
                return;
            case StartResultKind.LowMemory:
            {
                var go = await AskAsync(AlertKind.Warning, "可用内存偏少",
                    $"可用内存仅 {result.MemoryGb:0.0} GB。\n\n上百 GB 的维基全文搜索较吃内存，可能启动失败或被系统回收。\n\n仍要继续吗？",
                    "继续", "取消");
                if (!go) return;
                result = await _service.StartAsync(wanted);
                break;
            }
            case StartResultKind.LaunchFailed:
                SetStatus("启动失败", "error");
                await NotifyAsync(AlertKind.Error, "启动失败",
                    result.Message + "\n\n应用没有修改任何文件或系统设置。");
                return;
            case StartResultKind.DiedDuringStartup:
                // The single most confusing failure there is: the process came up
                // and quit before serving anything. Say so plainly, and show what
                // kiwix-serve printed, because the exit code alone means nothing.
                SetStatus("启动失败，详见下方提示", "error");
                SetBadge("未运行", "Idle");
                await NotifyAsync(AlertKind.Error, "服务启动后立即退出",
                    $"kiwix-serve 启动了，但在开始提供服务前就退出了（返回码 {result.ExitCode}）。\n\n"
                  + "它的输出：\n" + (string.IsNullOrWhiteSpace(result.Message) ? "（无输出）" : result.Message)
                  + "\n\n常见原因：\n· 杀毒软件拦截了 kiwix-serve.exe\n· 资料库文件不完整\n· 系统资源不足\n\n应用不会自动重试。");
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
            await NotifyAsync(AlertKind.Warning, "端口已顺延",
                $"端口 {wanted} 已被其它程序占用，本应用没有结束它，已自动改用 {result.Port}。\n\n"
              + $"若 {wanted} 上是上次运行残留的服务，可先点「停止」再重新启动。");
        }
        await SelfCheckAsync(result.Port);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_activePort is not { } port || !BundleLayout.HttpAlive(port))
        {
            SetStatus("服务未在运行，先启动服务。", "warn");
            return;
        }

        // Open loopback, not the LAN address shown above. The browser is on this
        // machine, and Windows Firewall blocks inbound connections by default,
        // so http://<lan-ip>:<port>/ can be unreachable from the very machine
        // serving it. Loopback is never firewalled.
        //
        // kiwix's index script also auto-applies a language filter derived from
        // the browser UI language when the URL carries no fragment, and persists
        // it in a cookie, which hides half of a bilingual library. The empty
        // "#lang=" fragment skips that entirely.
        OpenUrl($"http://127.0.0.1:{port}/#lang=");
    }

    private async void OnCopyUrlClick(object? sender, RoutedEventArgs e)
    {
        var text = UrlText.Text ?? string.Empty;
        try
        {
            // Clipboard is null until the top level has a platform clipboard
            // behind it, which is not guaranteed on a machine where another
            // process is holding it open.
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            await clipboard.SetTextAsync(text);
            SetStatus("已复制: " + text, "ok");
        }
        catch { /* clipboard can be locked by another process; nothing to do */ }
    }

    private async void OnVerifyClick(object? sender, RoutedEventArgs e)
    {
        // On demand only. Hashing the whole library off a USB stick takes
        // minutes, and doing it at launch is how a launcher gets blamed for
        // "hanging".
        if (_service.Library().Count == 0 || _verifyCts is not null) return;
        _verifyCts = new CancellationTokenSource();

        VerifyButton.IsEnabled = false;
        VerifyProgress.IsVisible = true;
        VerifyProgress.Value = 0;
        SetStatus("正在校验 SHA256，请勿拔出存储设备 ...", "busy");

        var progress = new Progress<(string Name, double Fraction, string Result)>(p =>
        {
            if (p.Fraction > 0) VerifyProgress.Value = Math.Clamp(p.Fraction * 100, 0, 100);
        });

        try
        {
            await _service.VerifyAsync(progress, _verifyCts.Token);
            SetStatus(_service.Library().Any(z => z.IntegrityState == PreflightState.Fail)
                ? "校验完成：有文件不匹配，请查看列表"
                : "校验完成", _service.Library().Any(z => z.IntegrityState == PreflightState.Fail) ? "error" : "ok");
        }
        catch (OperationCanceledException)
        {
            SetStatus("校验已取消", "warn");
        }
        finally
        {
            VerifyButton.IsEnabled = true;
            VerifyProgress.IsVisible = false;
            _verifyCts.Dispose();
            _verifyCts = null;
            RefreshLibrary();
        }
    }

    // ------------------------------------------------------------------ state

    private void OnRunning(int port)
    {
        _startedAt = DateTime.Now;
        _healthOk = false;
        _activePort = port;
        // The LAN address is what other devices need, so that is what we show.
        // Opening the browser goes through loopback instead -- see OnOpenClick.
        UrlText.Text = $"http://{BundleLayout.LocalIp()}:{port}";
        PortBox.Text = port.ToString();
        PortBox.IsEnabled = false;
        SetBadge("运行中", "Ok");
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        OpenButton.IsEnabled = true;
        CopyUrlButton.IsEnabled = true;
        SetStatus($"运行中 · 本机请用 http://127.0.0.1:{port}/#lang= 打开，其他设备用上方地址", "ok");
        StartHealthLoop();
    }

    private void OnExited(int code)
    {
        _startedAt = null;
        _healthOk = false;
        _activePort = null;
        StopHealthLoop();
        UrlText.Text = "—";
        RuntimeText.Text = "";
        PortBox.IsEnabled = true;
        SetBadge("未运行", "Idle");
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        CopyUrlButton.IsEnabled = false;

        if (code is 0 or -15)
        {
            SetStatus("服务已停止");
            return;
        }

        AppendLog($"!! 进程异常退出，返回码 {code}");
        SetStatus("服务未能持续运行，请查看日志", "error");
        _ = NotifyAsync(AlertKind.Error, "服务已停止",
            $"kiwix-serve 未能持续运行（退出码 {code}）。\n\n常见原因：\n· 端口被占用\n· 资料库不完整或版本不兼容\n· 存储设备被系统以 noexec 挂载\n\n应用不会自动重试，也不会修改你的文件。");
    }

    private void OnHealth(bool ok)
    {
        if (ok && !_healthOk) AppendLog("本机健康检查通过：HTTP 服务已响应");
        _healthOk = ok;
    }

    private void StartHealthLoop()
    {
        StopHealthLoop();
        _healthCts = new CancellationTokenSource();
        var token = _healthCts.Token;
        // Off the UI thread on purpose: the probe opens a TCP connection with a
        // timeout, and doing that inline would freeze the window every 3s.
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (token.IsCancellationRequested || _activePort is not { } port) continue;
                bool ok;
                try { ok = await Task.Run(() => BundleLayout.HttpAlive(port, 900), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                Dispatcher.UIThread.Post(() => OnHealth(ok));
            }
        }, CancellationToken.None);
    }

    private void StopHealthLoop()
    {
        _healthCts?.Cancel();
        _healthCts?.Dispose();
        _healthCts = null;
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
            SetStatus($"已加载 {loaded}/{expected} 本 · 本机已响应（外网/其他设备可达性取决于防火墙）", "ok");
        }
        else
        {
            AppendLog($"! 自检异常：磁盘上有 {expected} 本，服务器只加载了 {loaded} 本");
            SetStatus($"只加载了 {loaded}/{expected} 本，请查看日志", "error");
            await NotifyAsync(AlertKind.Warning, "资料库未被全部加载",
                $"磁盘上有 {expected} 个 .zim，但服务器只加载了 {loaded} 个。\n\n可能是某个文件不完整或被截断。应用不会修改你的文件。");
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser to open; the address is still on screen to copy */ }
    }

    // ------------------------------------------------------------------ alerts

    /// <summary>
    /// Show a sheet and take the overlay layer down again once it is answered.
    ///
    /// The layer itself has to be toggled: it is an empty Panel for the rest of
    /// the window's life, and a visible empty Panel stretched over everything
    /// would swallow every click, while an invisible one would hide the sheet
    /// along with it. The alert only sets its own IsVisible, so the layer is
    /// this window's job.
    /// </summary>
    private async Task<bool> AskAsync(AlertKind kind, string title, string message,
                                      string confirmText, string cancelText = "取消",
                                      bool destructive = false)
    {
        AlertLayer.IsVisible = true;
        try { return await _alert.ConfirmAsync(kind, title, message, confirmText, cancelText, destructive); }
        finally { AlertLayer.IsVisible = false; }
    }

    private async Task NotifyAsync(AlertKind kind, string title, string message, string okText = "好")
    {
        AlertLayer.IsVisible = true;
        try { await _alert.NotifyAsync(kind, title, message, okText); }
        finally { AlertLayer.IsVisible = false; }
    }

    // ------------------------------------------------------------------ closing

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed) return;

        // Veto first, ask second. The dialog is async, so the close cannot be
        // allowed to proceed before the user has answered it.
        e.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_service.IsRunning)
        {
            var go = await AskAsync(AlertKind.Warning, "退出",
                "服务还在运行。\n\n关闭本窗口会一并停止它，确定吗？", "退出并停止", "取消", destructive: true);
            if (!go) { _closePromptOpen = false; return; }
        }

        _closePromptOpen = false;
        _closeConfirmed = true;
        _verifyCts?.Cancel();
        StopHealthLoop();
        _clock.Stop();
        _service.Dispose();
        Close();
    }
}
