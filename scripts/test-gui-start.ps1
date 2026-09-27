# ============================================================
#  End-to-end test of the WinUI 3 launcher, driven through UI
#  Automation: it presses the same buttons a user presses.
#
#  Usage:
#     powershell -File scripts\test-gui-start.ps1 [-Bundle <dir>] [-Exe <path>]
#
#  Why this exists
#  --------------
#  Launching the window and running --selftest both pass while the start
#  button is completely broken. That is not hypothetical: v1.1.0 shipped a
#  launcher whose OnRunning() was dead code, so pressing 启动服务 started
#  kiwix-serve invisibly, left the badge on "未运行" and kept 打开书架
#  permanently disabled. Nothing caught it because nothing ever clicked.
#
#  A real library is needed for the full assertion set. Without one the
#  script still presses the button and checks the app refuses cleanly
#  instead of pretending to be running.
# ============================================================
param(
    [string]$Bundle = '',
    [string]$Exe = '',
    [int]$Port = 8092,
    [int]$SettleSeconds = 30
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Locate the bundle the way the application does: walk up from the executable
# until a directory holds both zim\ and app\. Guessing it relative to this
# script instead only worked while the checkout and the bundle sat in the same
# parent directory, and it failed silently -- the test went on to Start-Process
# with a working directory that does not exist and died on a DirectoryNotFound
# that says nothing about the GUI. The checkout now lives on a different volume
# from the bundle, which is exactly the case the guess could not survive.
function Find-Bundle([string]$StartDir) {
    $dir = $StartDir
    while ($dir) {
        if ((Test-Path (Join-Path $dir 'zim')) -and (Test-Path (Join-Path $dir 'app'))) { return $dir }
        $parent = Split-Path -Parent $dir
        if (-not $parent -or $parent -eq $dir) { break }
        $dir = $parent
    }
    return $null
}

if ($Bundle -and -not $Exe) {
    # A caller who named the bundle has told us everything; the executable is a
    # fixed path inside it. Checked in this order so -Bundle on its own is enough.
    if (-not (Test-Path -LiteralPath $Bundle)) { throw "no such bundle directory: $Bundle" }
    $Exe = Join-Path $Bundle 'app\gui\KiwixWinUI\KiwixWinUI.exe'
    if (-not (Test-Path -LiteralPath $Exe)) { throw "no KiwixWinUI.exe inside the bundle: $Exe" }
} elseif (-not $Exe) {
    # Neither given. The bundle is not part of the repository -- it holds the
    # 148 GB of ZIM files and deliberately lives elsewhere -- so the only honest
    # thing to do is try the one place a bundle has ever sat next to a checkout,
    # and otherwise say so.
    $guess = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Kiwix-USB'
    if (Test-Path -LiteralPath (Join-Path $guess 'app\gui\KiwixWinUI\KiwixWinUI.exe')) {
        $Exe = Join-Path $guess 'app\gui\KiwixWinUI\KiwixWinUI.exe'
    } else {
        throw ("no -Exe or -Bundle given, and no bundle beside this checkout at $guess. " +
               "The bundle is not part of the repository; pass -Exe <path> or -Bundle <dir>.")
    }
}

if (-not $Bundle) {
    $found = Find-Bundle (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $Exe)))
    if (-not $found) { throw "could not find a bundle (a directory with zim\ and app\) above $Exe; pass -Bundle" }
    $Bundle = $found
} elseif (-not (Test-Path -LiteralPath $Bundle)) {
    throw "no such bundle directory: $Bundle"
}

$script:failures = 0
function Check($ok, [string]$what, [string]$detail = '') {
    if ($ok) { Write-Host ("  [PASS] {0}{1}" -f $what, $(if ($detail) { "  $detail" })) }
    else { Write-Host ("  [FAIL] {0}{1}" -f $what, $(if ($detail) { "  $detail" })); $script:failures++ }
}

Add-Type -Namespace GW -Name U -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
"@

function Get-MainWindow([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    foreach ($e in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($e.Current.Name -and $e.Current.Name -like '*Kiwix*') { return $e }
    }
    return $null
}

function Get-ById($root, [string]$id) {
    if (-not $root) { return $null }
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-Text($root, [string]$id) {
    $e = Get-ById $root $id
    if (-not $e) { return "<missing $id>" }
    # TextBox exposes ValuePattern; a TextBlock does not and only has Name.
    try { return $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    catch { }
    try { return $e.Current.Name } catch { }
    return "<unreadable $id>"
}

function Invoke-Button($root, [string]$id) {
    $e = Get-ById $root $id
    if (-not $e) { return "no $id" }
    if ($e.Current.IsEnabled -ne $true) { return "disabled" }
    try { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "invoked" }
    catch { return "invoke failed" }
}

function Get-PortOpen([int]$p) {
    $c = New-Object Net.Sockets.TcpClient
    $ok = $false
    try { $null = $c.ConnectAsync('127.0.0.1', $p).Wait(900); $ok = $c.Connected } catch { }
    $c.Dispose()
    return $ok
}

$zims = @(Get-ChildItem (Join-Path $Bundle 'zim') -Filter '*.zim' -ErrorAction SilentlyContinue)
Write-Host "=== KiwixWinUI GUI end-to-end test ==="
Write-Host "  bundle : $Bundle"
Write-Host "  exe    : $Exe"
Write-Host "  library: $($zims.Count) zim file(s)"
if (-not (Test-Path -LiteralPath $Exe)) { throw "not found: $Exe" }

Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process KiwixWinUI -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $Exe -WorkingDirectory $Bundle -PassThru
Write-Host "  pid    : $($proc.Id)"

$root = $null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and -not $root) {
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { break }
    $root = Get-MainWindow $proc.Id
}
Check ($null -ne $root) "main window appears"
if (-not $root) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; exit 1 }
Write-Host "  window : $($root.Current.Name)"

Check ((Get-Text $root 'BadgeText') -match '未运行') "starts in the not-running state"
# The header reports the kiwix-tools version by asking the bundled binary, so a
# failed probe must not pass unnoticed. Whether a number is *required* depends on
# whether a binary is actually reachable. "未知" is the correct display when there
# is nothing to ask -- a CI runner has no kiwix-serve at all, and demanding a
# number there would assert a lie.
#
# $Bundle was resolved once at the top by walking up from the executable, the same
# rule the application uses. Re-deriving it here is what let the two disagree:
# this copy silently fell back to the executable's own directory and reported a
# bundle the test was not actually running against.
$serveName = if ($env:OS -eq 'Windows_NT') { 'kiwix-serve.exe' } else { 'kiwix-serve' }
$hasServe = @(Get-ChildItem (Join-Path $Bundle 'app') -Recurse -Filter $serveName -File -ErrorAction SilentlyContinue).Count -gt 0
$ver = Get-Text $root 'VersionText'
if ($hasServe) {
    Check ($ver -match 'kiwix-tools\s+\d') "header reports the probed kiwix-tools version", $ver
} else {
    Write-Host "  -- no kiwix-serve under $Bundle, so 未知 is expected --"
    Check ($ver -match 'kiwix-tools\s+未知') "header says 未知 when there is no binary to ask", $ver
}
# Compare against the declared version rather than just checking that a number is
# there: the window once displayed v1.1.2 inside a v1.2.0 bundle, and a regex
# would have called that a pass.
#
# The number lives in src/Directory.Build.props, not in this project's csproj.
# There are three executables in a bundle and MSBuild picks that file up
# automatically for every project under src/, which is what stops them drifting
# apart. Reading the csproj here instead reported "unknown" and failed the build
# for the right reason at the wrong file.
$props = Join-Path (Split-Path -Parent $Exe) '..\..\..\..\src\Directory.Build.props'
if (-not (Test-Path $props)) { $props = Join-Path $PSScriptRoot '..\src\Directory.Build.props' }
$expect = 'unknown'
if (Test-Path $props) {
    $m = [regex]::Match((Get-Content $props -Raw), '<Version>([^<]+)</Version>')
    if ($m.Success) { $expect = $m.Groups[1].Value.Trim() }
}
if ($expect -eq 'unknown') { throw "could not read <Version> from $props" }
Check ($ver -match ('v' + [regex]::Escape($expect) + '\b')) "header version matches the declared version", "want v$expect, header: $ver"
Check ((Invoke-Button $root 'StartButton') -eq 'invoked') "启动服务 can be pressed"

Start-Sleep -Seconds 4
$root = Get-MainWindow $proc.Id
$badge = Get-Text $root 'BadgeText'
$status = Get-Text $root 'StatusText'
$log = Get-Text $root 'LogBox'

if ($zims.Count -eq 0) {
    Write-Host "  -- no library, so the running-state assertions are skipped --"
    Check ($badge -match '未运行') "stays not-running with an empty library", "badge=$badge"
    # A refused start must leave a usable window. This is what a CI runner can
    # check, and it is worth checking: a start button that stays disabled after
    # a failure needs a restart to recover.
    Start-Sleep -Seconds 4
    $root = Get-MainWindow $proc.Id
    Check ($null -ne $root) "GUI survives a refused start"
    $sb = Get-ById $root 'StartButton'
    Check ($null -ne $sb -and $sb.Current.IsEnabled) "start button is usable again after a refused start"
} else {
    $up = $false
    # The state has to be read *after* the service is up, not right after the
    # click: kiwix-serve needs a moment to bind while it opens the library.
    $deadline = (Get-Date).AddSeconds($SettleSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Get-PortOpen $Port) { $up = $true; break }
        $root = Get-MainWindow $proc.Id
        if (-not $root) { break }
    }
    $root = Get-MainWindow $proc.Id
    $badge = Get-Text $root 'BadgeText'

    Check ($badge -match '运行中') "badge switches to 运行中", "badge=$badge"
    Check ((Get-Text $root 'UrlText') -match "^http://") "an address is displayed", (Get-Text $root 'UrlText')
    Check ((Get-Text $root 'RuntimeText') -match 'PID') "uptime/PID line appears", (Get-Text $root 'RuntimeText')
    Check ((Get-Text $root 'LogBox') -match '自检通过|已加载') "self check reports the book count"
    Check ($up) "kiwix-serve is listening on the port"

    $open = Get-ById $root 'OpenButton'
    Check ($null -ne $open -and $open.Current.IsEnabled) "打开书架 is enabled while running"
    Check ((Invoke-Button $root 'OpenButton') -eq 'invoked') "打开书架 can be pressed"

    # Surviving a while is the difference between "started" and "stays up".
    Start-Sleep -Seconds 10
    $root = Get-MainWindow $proc.Id
    Check ($null -ne $root) "GUI is still alive after 10s"
    Check ((Get-Text $root 'BadgeText') -match '运行中') "still 运行中 after 10s"
    Check (Get-PortOpen $Port) "port still open after 10s"
}

Stop-Process -Name KiwixWinUI -Force -ErrorAction SilentlyContinue
Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failures -eq 0) { Write-Host "RESULT: PASS"; exit 0 }
Write-Host "RESULT: FAIL ($script:failures)"; exit 1
