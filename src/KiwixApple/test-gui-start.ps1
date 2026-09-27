# ============================================================
#  End-to-end test of the Avalonia launcher, driven through
#  Windows UI Automation: it presses the same buttons a user
#  presses, by the same AutomationId the project publishes.
#
#  Usage:
#     powershell -File test-gui-start.ps1 [-Exe <path>] [-Port 8092]
#
#  Why this exists
#  ---------------
#  Launching the window and running --selftest both pass while the
#  start button is completely broken. v1.1.0 of the WinUI launcher
#  shipped with an OnRunning() that was dead code, so pressing start
#  ran kiwix-serve invisibly, left the badge on "not running" and
#  kept the open button disabled. Nothing caught it because nothing
#  ever clicked.
#
#  Every interactive control and every status/log text block in
#  MainWindow.axaml therefore carries an explicit
#  AutomationProperties.AutomationId. This script is the reason.
# ============================================================
param(
    [string]$Exe = '',
    [int]$Port = 8092,
    [int]$SettleSeconds = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not $Exe) { $Exe = Join-Path $PSScriptRoot 'dist\KiwixApple.exe' }

$script:failures = 0
function Check($ok, [string]$what, [string]$detail = '') {
    $mark = if ($ok) { '[PASS]' } else { '[FAIL]' }
    Write-Host ("  {0} {1}{2}" -f $mark, $what, $(if ($detail) { "  $detail" }))
    if (-not $ok) { $script:failures++ }
}

Add-Type -Namespace GW -Name U -MemberDefinition @"
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
"@

function Get-MainWindow([int]$procId) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    foreach ($e in [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $cond)) {
        if ($e.Current.Name) { return $e }
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
    # TextBox exposes ValuePattern; a TextBlock only has Name.
    try { return $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value }
    catch { }
    try { return $e.Current.Name } catch { }
    return "<unreadable $id>"
}

function Invoke-Button($root, [string]$id) {
    $e = Get-ById $root $id
    if (-not $e) { return "no $id" }
    try {
        $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return "invoked"
    }
    catch { return "invoke failed" }
}

function Get-PortOpen([int]$p) {
    $c = New-Object Net.Sockets.TcpClient
    $ok = $false
    try { $null = $c.ConnectAsync('127.0.0.1', $p).Wait(900); $ok = $c.Connected } catch { }
    $c.Dispose()
    return $ok
}

# An alert sheet is modal and the start handler is still awaiting the answer, so
# anything asserted about button state has to come after the sheet is dismissed.
# 597D is 好, the acknowledge action every sheet ends with.
function Dismiss-Sheet($root) {
    $btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button)))
    foreach ($b in $btns) {
        if ($b.Current.Name -eq ([char]0x597D)) {
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Start-Sleep -Seconds 2
            return $true
        }
    }
    return $false
}

# Resolve the bundle the way the app does: walk up for zim\ + app\.
$exeDir = (Resolve-Path (Split-Path -Parent $Exe)).Path
$bundleRoot = $exeDir
for ($i = 0; $i -lt 6; $i++) {
    if ((Test-Path (Join-Path $bundleRoot 'zim')) -and (Test-Path (Join-Path $bundleRoot 'app'))) { break }
    $parent = Split-Path -Parent $bundleRoot
    if (-not $parent -or $parent -eq $bundleRoot) { $bundleRoot = $exeDir; break }
    $bundleRoot = $parent
}
$zims = @(Get-ChildItem (Join-Path $bundleRoot 'zim') -Filter '*.zim' -ErrorAction SilentlyContinue)

Write-Host "=== KiwixApple GUI end-to-end test ==="
Write-Host "  exe     : $Exe"
Write-Host "  bundle  : $bundleRoot"
Write-Host "  library : $($zims.Count) zim file(s)"
if (-not (Test-Path -LiteralPath $Exe)) { throw "not found: $Exe" }

Get-Process kiwix-serve  -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process KiwixApple  -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$proc = Start-Process -FilePath $Exe -WorkingDirectory $bundleRoot -PassThru
Write-Host "  pid     : $($proc.Id)"

$root = $null
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline -and -not $root) {
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { break }
    $root = Get-MainWindow $proc.Id
}
Check ($null -ne $root) "main window appears"
if (-not $root) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; exit 1 }

# The title is a contract, not decoration: the automation test finds the window
# by it, and a user sees it in the taskbar and the alt-tab list.
Check ($root.Current.Name -eq 'Kiwix 离线维基 · 便携版') "window title is exact" $root.Current.Name

foreach ($id in @('StartButton','StopButton','OpenButton','CopyUrlButton','VerifyButton','PortBox',
                 'LogBox','StatusText','BadgeText','RuntimeText','VersionText','UrlText')) {
    Check ($null -ne (Get-ById $root $id)) "control is reachable by AutomationId" $id
}

$ver = Get-Text $root 'VersionText'
Check ($ver -match 'kiwix-tools\s+3\.8') "header reports the probed kiwix-tools version" $ver

# Read the declared version instead of hardcoding it. All three executables in a
# bundle share one number from src/Directory.Build.props, and a literal here went
# stale the moment the Avalonia build stopped claiming v1.0.0 while the WinUI
# build next to it claimed v1.2.1. Comparing against the declaration is the whole
# point: a regex that merely found a number would pass on a wrong one.
$propsFile = Join-Path $PSScriptRoot '..\Directory.Build.props'
$expect = $null
if (Test-Path $propsFile) {
    $mv = [regex]::Match((Get-Content $propsFile -Raw), '<Version>([^<]+)</Version>')
    if ($mv.Success) { $expect = $mv.Groups[1].Value.Trim() }
}
if (-not $expect) { throw "could not read <Version> from $propsFile" }
Check ($ver -match ('v' + [regex]::Escape($expect) + '\b')) "header version matches the declared version" "want v$expect, got: $ver"

Check ((Get-Text $root 'BadgeText') -eq '未运行') "starts in the not-running state" (Get-Text $root 'BadgeText')
Check ((Invoke-Button $root 'StartButton') -eq 'invoked') "start can be pressed"

Start-Sleep -Seconds 4
$root = Get-MainWindow $proc.Id
Write-Host "  status  : $(Get-Text $root 'StatusText')"

if ($zims.Count -eq 0) {
    Write-Host "  -- no library, so the running-state assertions are skipped --"
    Check ((Get-Text $root 'BadgeText') -eq '未运行') "stays not-running with an empty library"
    Start-Sleep -Seconds 4
    $root = Get-MainWindow $proc.Id
    Check ($null -ne $root) "GUI survives a refused start"
    # The refusal comes back as a modal sheet, and the start handler is still
    # awaiting the answer: without dismissing it the button would read disabled
    # for no reason at all.
    Check (Dismiss-Sheet $root) "the refusal is explained in an alert sheet, not a MessageBox"
    $root = Get-MainWindow $proc.Id
    Start-Sleep -Seconds 2
    $sb = Get-ById $root 'StartButton'
    Check ($null -ne $sb -and $sb.Current.IsEnabled) "start button is usable again after a refused start"
    Check ($null -ne $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
              (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)))) "no nested MessageBox appeared"
} else {
    # The state has to be read *after* the service is up, not right after the
    # click: kiwix-serve needs a moment to bind while it opens a 118 GB library.
    $up = $false
    $deadline = (Get-Date).AddSeconds($SettleSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        if (Get-PortOpen $Port) { $up = $true; break }
        $root = Get-MainWindow $proc.Id
        if (-not $root) { break }
    }
    $root = Get-MainWindow $proc.Id

    Check ((Get-Text $root 'BadgeText') -eq '运行中') "badge switches to running" (Get-Text $root 'BadgeText')
    Check ((Get-Text $root 'UrlText') -match '^http://') "a LAN address is displayed" (Get-Text $root 'UrlText')
    Check ((Get-Text $root 'RuntimeText') -match 'PID') "uptime/PID line appears" (Get-Text $root 'RuntimeText')
    Check ((Get-Text $root 'StatusText') -match '已加载') "self check reports the book count" (Get-Text $root 'StatusText')
    Check ((Get-Text $root 'LogBox') -match '自检通过|已加载') "log records the book count"
    Check $up "kiwix-serve is listening on port $Port"

    $open = Get-ById $root 'OpenButton'
    Check ($null -ne $open -and $open.Current.IsEnabled) "open is enabled while running"
    Check ((Invoke-Button $root 'OpenButton') -eq 'invoked') "open can be pressed"

    # Surviving a while is the difference between "started" and "stays up".
    Start-Sleep -Seconds 10
    $root = Get-MainWindow $proc.Id
    Check ($null -ne $root) "GUI is still alive after 10s"
    Check ((Get-Text $root 'BadgeText') -eq '运行中') "still running after 10s"
    Check (Get-PortOpen $Port) "port still open after 10s"

    # A stale sheet must never be left hanging over the window, and a
    # MessageBox would show up here as a nested top-level window.
    Check (-not (Dismiss-Sheet $root)) "no alert sheet is left open in the steady state"
    Check ($null -eq $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
              (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Window)))) "no MessageBox is used anywhere"

    Check ((Invoke-Button $root 'StopButton') -eq 'invoked') "stop can be pressed"
    Start-Sleep -Seconds 5
    $root = Get-MainWindow $proc.Id
    Check ($null -ne $root) "GUI survives the service exiting"
    Check ((Get-Text $root 'BadgeText') -eq '未运行') "badge resets to not-running" (Get-Text $root 'BadgeText')
    $sb = Get-ById $root 'StartButton'
    Check ($null -ne $sb -and $sb.Current.IsEnabled) "start is usable again after a stop"
}

Stop-Process -Name KiwixApple -Force -ErrorAction SilentlyContinue
Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ""
if ($script:failures -eq 0) { Write-Host "RESULT: PASS"; exit 0 }
Write-Host "RESULT: FAIL ($script:failures)"; exit 1
