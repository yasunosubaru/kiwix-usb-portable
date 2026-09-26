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

Write-Host "[1/3] dotnet publish"
& dotnet publish $Project -c $Configuration -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[2/3] verify output"
$required = @('KiwixWinUI.exe', 'KiwixWinUI.dll', 'KiwixWinUI.runtimeconfig.json')
foreach ($f in $required) {
    $p = Join-Path $Output $f
    if (-not (Test-Path -LiteralPath $p)) { throw "missing $f" }
    Write-Host ("      OK {0}  {1:N2} MB" -f $f, ((Get-Item $p).Length / 1MB))
}

$files = Get-ChildItem $Output -Recurse -File
Write-Host ("      {0} files, {1:N1} MB total" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

Write-Host "[3/3] launch check (no library needed)"
$proc = Start-Process (Join-Path $Output 'KiwixWinUI.exe') -PassThru
Start-Sleep -Seconds 12
$alive = Get-Process KiwixWinUI -ErrorAction SilentlyContinue
if ($alive) {
    Write-Host "      OK window process alive (PID $($alive[0].Id))"
    Stop-Process -Name KiwixWinUI -Force -ErrorAction SilentlyContinue
} else {
    throw "app did not stay alive"
}
Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done: $Output"
