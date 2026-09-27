# KiwixApple — the Apple-style launcher

A third desktop GUI for the `kiwix-usb-portable` bundle, written in **Avalonia**
and styled to **Apple's Human Interface Guidelines**. It starts the bundled
`kiwix-serve` on a port, then the user browses the offline Wikipedia in an
ordinary browser.

Feature parity with `src/KiwixWinUI` (WinUI 3) and `src/KiwixUSB.py` (tkinter).
All three front-ends resolve the same bundle and drive the same binary, so they
stay interchangeable.

---

## Why Avalonia and not UIKit

The request was "Apple's UIKit". UIKit ships only inside the iOS / tvOS / macOS
SDKs. There is no Windows toolchain that can compile against it and emit a
`.exe`, and no Microsoft toolchain that will ever produce one. So there is no
UIKit code in this project and this project makes no claim to be UIKit.

What it *is*: UIKit's **design language and interaction idiom**, reproduced on a
framework that can actually emit a Windows executable. Every decision below is
about the design language, not the framework.

| UIKit idiom | Where it lives here |
|---|---|
| Large title that collapses to a small inline title on scroll | `Views/MainWindow.axaml` — the 34pt title lives *inside* the scroll content so it leaves on its own; `UpdateNavChrome()` cross-fades the 17pt inline title, the opaque backdrop and the hairline over 28pt of travel, which is the same ramp UIKit uses. |
| Inset grouped list | `Theme/AppleTheme.axaml` — `Border.group` is 10pt corners on `secondarySystemGroupedBackground`. Separators are 1px hairlines that are a plain full-width child of the group's *padded* content box, so they start exactly at the text origin without a magic inset value. |
| Grouped section headers | `TextBlock.sectionHeader` — 13pt semibold, 0.6pt tracking, `secondaryLabel`. The uppercase transform is in `UpperCaseHeaders()` because Avalonia 11 has no `TextTransform` on `TextBlock`; Chinese has no case, so the tracking is what carries the style. |
| Semantic colours | `App.axaml` `ThemeDictionaries`, declared twice. Dark is not an inversion: Apple re-derives the accents for a black canvas (`#0A84FF`, not `#007AFF`) and the greys carry alpha in both appearances. |
| SF type ramp | `Theme/AppleMetrics.axaml` — 34 / 28 / 22 / 20 / 17 / 17 / 16 / 15 / 13 / 12 / 11 with the guideline weights. |
| Standard metrics | 8 / 10 / 12pt radii, 16pt screen gutter, 8pt grid, 44pt minimum touch target on every interactive row and button. |
| Filled / tinted / plain buttons | `Button.filled`, `Button.tinted`, `Button.plain`, plus `Button.alertAction` for sheet actions. |
| `UIAlertController` | `Views/AppleAlert.axaml` — a centred sheet over a scrim, with a stroked glyph, a semibold title, a hairline and stacked tinted actions. Not a `MessageBox`, which is system chrome that cannot carry an icon, cannot follow the dark appearance and cannot be styled at all. |
| No WPF / WinUI leftovers | `Theme/AppleControls.axaml` replaces the `ControlTheme` for `Button` and `TextBox` outright. That is not decoration: Fluent restyles its template *parts* on `:pointerover`, `:pressed` and `:focus`, and a setter on a part outranks the `TemplateBinding` that would have carried our colour down — so a filled blue button flashes grey the moment the mouse touches it unless the template is owned. |

### Light and dark

`RequestedThemeVariant="Default"` — the window follows the system, unlike the
WinUI front-end which is hard-coded dark. Both appearances were verified by
rendering each one.

---

## Safety boundaries

These are the product's contract, not caution for its own sake. Each one is
enforced in code and commented where the trap is.

* **Never elevates.** `app.manifest` pins `requestedExecutionLevel="asInvoker"`.
  Nothing in the app ever calls for elevation. If the window *is* running
  elevated anyway, it says so in the log rather than pretending otherwise.
* **Never kills a foreign process.** If the preferred port is busy, the service
  moves to the next free one and logs that it did not end the incumbent.
  `Process.Kill` is only ever reached through a handle this app created.
* **Never creates a scheduled task, service or autostart entry.**
* **Never touches the Windows Firewall.** That is also why the LAN address is
  shown but the *open* action uses loopback: a machine serving the pages may not
  be able to reach its own LAN address, because the firewall blocks inbound.
* **Never makes a network request off this machine.** `AssertLoopback` in
  `Core/KiwixService.cs` asserts it on every HTTP call rather than assuming it,
  so a future edit that points the catalog probe at a remote host fails loudly.
* **Treats `zim\` as strictly read-only.** The only file opens in the project are
  `FileAccess.Read, FileShare.Read`, and the window says so in the log.

---

## Two bugs this project is shaped around

Both are documented at the code that guards them.

1. **A live process is not a serving server.** `StartCoreAsync` waits up to 25s
   for the port to actually accept a connection, and if the child quits first it
   reports the exit code *and* the child's captured output. A previous release
   shipped a GUI that reported success while the server had already died.

2. **A `Process` that exits during startup is read from two threads at once.**
   `Exited` is raised on a threadpool thread while the startup probe is in its
   wait loop. Disposing the `Process` in the handler destroys the `ExitCode` the
   probe is about to report and throws `InvalidOperationException` — turning the
   one failure the user most needs explained into a crash of the launcher. The
   handler therefore *retires* the child and frees the handle later, from
   whoever can be certain nobody will ask again.

---

## Automation-testable UI

Every interactive control and every status / log text block carries an explicit
`AutomationProperties.AutomationId`, so the end-to-end test can find them with
Windows UI Automation and press the real buttons:

`StartButton`, `StopButton`, `OpenButton`, `CopyUrlButton`, `VerifyButton`,
`PortBox`, `LogBox`, `StatusText`, `BadgeText`, `RuntimeText`, `VersionText`,
`UrlText`, plus `Preflight_<key>` and `Library_<name>` on generated rows.

Launching the window and running `--selftest` both pass while the start button
is completely broken, so something has to actually click.

Window title is exactly `Kiwix 离线维基 · 便携版`.

---

## Build and test

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o dist
```

or, which also runs both tests against a real bundle:

```powershell
powershell -File build-apple.ps1
```

Headless verification, no display needed:

```powershell
dist\KiwixApple.exe --selftest
```

`--selftest` runs on a dedicated foreground thread with **no**
`SynchronizationContext` and never blocks the UI thread on it: the self-test
blocks on `GetAwaiter().GetResult()` and every awaited `HttpClient`
continuation is posted to the captured context, so awaiting it on a pumping
context is a hard deadlock. It prints `[PASS]` / `[FAIL]` lines and a final
`RESULT: PASS` / `RESULT: FAIL`, and exits 0 or 1. As a `WinExe` it has no
console of its own, so `Core/ConsoleBridge.cs` borrows the parent's, and writes
UTF-8 directly to the handle when stdout is a pipe — otherwise every Chinese log
line comes out as mojibake through the OEM code page.

End-to-end UI test:

```powershell
powershell -File test-gui-start.ps1 -Exe <path-to-KiwixApple.exe>
```

The published exe has to sit somewhere inside a bundle for either test to
resolve `zim\` + `app\`; `build-apple.ps1` stages it, runs the tests and removes
it again.

---

## Layout

```
KiwixApple.csproj        net8.0-windows, win-x64, self-contained, WinExe
app.manifest             asInvoker, PerMonitorV2
Program.cs               --selftest diverts before a lifetime is created
App.axaml                semantic colour ThemeDictionaries (light + dark)
Core/BundleLayout.cs     bundle discovery, platform, port probing, memory
Core/KiwixService.cs     child process lifecycle, self check, SHA256 verify
Core/SelfTest.cs         headless verification
Core/ConsoleBridge.cs    stdout for a WinExe
Theme/AppleMetrics.axaml HIG constants and the SF type ramp
Theme/AppleControls.axaml the Button and TextBox ControlThemes
Theme/AppleTheme.axaml   every other style
Views/AppleAlert.axaml   the UIAlertController equivalent
Views/MainWindow.axaml   the window
build-apple.ps1          publish + both tests
test-gui-start.ps1       UI Automation end-to-end test
```
