# ============================================================
#  Build the WinUI 3 launcher (Windows only).
#
#  Usage:
#     powershell -File scripts\build-winui.ps1 [-Configuration Release]
#
#  Produces a self-contained, unpackaged WinUI 3 app: no MSIX, no
#  install, no Windows App Runtime prerequisite. The cost is size --
#  roughly 166 MB because the .NET runtime and the Windows App
#  Runtime ship inside the output folder.
# ============================================================
param(
    [string]$Configuration = 'Release',
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root 'src\KiwixWinUI\KiwixWinUI.csproj'
if (-not $Output) { $Output = Join-Path $Root 'dist\KiwixWinUI' }

Write-Host "project : $Project"
Write-Host "output  : $Output"

Write-Host "[1/4] dotnet publish"
& dotnet publish $Project -c $Configuration -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[2/4] verify output"
$required = @('KiwixWinUI.exe', 'KiwixWinUI.dll', 'KiwixWinUI.runtimeconfig.json')
foreach ($f in $required) {
    $p = Join-Path $Output $f
    if (-not (Test-Path -LiteralPath $p)) { throw "missing $f" }
    Write-Host ("      OK {0}  {1:N2} MB" -f $f, ((Get-Item $p).Length / 1MB))
}

$files = Get-ChildItem $Output -Recurse -File
Write-Host ("      {0} files, {1:N1} MB total" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

Write-Host "[3/4] headless self-test"
$self = Start-Process (Join-Path $Output 'KiwixWinUI.exe') -ArgumentList '--selftest' -PassThru `
        -RedirectStandardOutput "$Output\selftest.log" -RedirectStandardError "$Output\selftest.err"
$null = $self.WaitForExit(300000)
Get-Content "$Output\selftest.log" | ForEach-Object { Write-Host "      $_" }
if ((Get-Content "$Output\selftest.log" -Raw) -notmatch 'RESULT: PASS') {
    throw "self-test did not pass; see $Output\selftest.log"
}

Write-Host "[4/4] GUI end-to-end test (presses the real buttons)"
# Launching the window and running --selftest both pass while the start button
# is completely broken, so the button path itself has to be driven. Skipped when
# the bundle has no library, because the service cannot start without one.
$zims = @(Get-ChildItem (Join-Path $Root '..\zim') -Filter '*.zim' -ErrorAction SilentlyContinue)
if ($zims.Count -eq 0) {
    Write-Warning "no .zim in the bundle, skipping the GUI end-to-end test"
} else {
    & (Join-Path $PSScriptRoot 'test-gui-start.ps1') -Exe (Join-Path $Output 'KiwixWinUI.exe') -Bundle (Resolve-Path (Join-Path $Root '..')).Path
    if ($LASTEXITCODE -ne 0) { throw "GUI end-to-end test failed" }
}

Write-Host ""
Write-Host "Done: $Output"
