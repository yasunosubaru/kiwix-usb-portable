# ============================================================
#  Build the Avalonia launcher (Windows only).
#
#  Usage:
#     powershell -File build-apple.ps1 [-Configuration Release]
#
#  Produces a self-contained win-x64 folder: no MSIX, no install,
#  no Windows App Runtime prerequisite. The cost is size, roughly
#  94 MB, almost all of it the .NET runtime and Skia.
#
#  PublishSingleFile is deliberately off. A single-file exe has to
#  unpack the native Skia and HarfBuzz binaries into %TEMP% on first
#  run, which fails outright on a stick that is mounted read-only or
#  on a machine where %TEMP% is not writable. The bundle ships a plain
#  folder, so the folder is the right shape.
# ============================================================
param(
    [string]$Configuration = 'Release',
    [string]$Output = '',
    [string]$Bundle = '',
    [switch]$SkipSelfTest,
    [switch]$SkipGuiTest
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$Root = $PSScriptRoot
$Project = Join-Path $Root 'KiwixApple.csproj'
if (-not $Output) { $Output = Join-Path $Root 'dist' }

# The bundle is a sibling of the repository, which is the same convention
# scripts\test-gui-start.ps1 uses. A repository that *is* a bundle layout is
# accepted too, so a checkout with zim\ and app\ committed works as well.
if (-not $Bundle) {
    $candidates = @(
        (Join-Path (Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $Root))) 'Kiwix-USB'),
        (Join-Path (Split-Path -Parent (Split-Path -Parent $Root)) 'zim')
    )
    $Bundle = $candidates | Where-Object {
        (Test-Path (Join-Path $_ 'zim')) -and (Test-Path (Join-Path $_ 'app'))
    } | Select-Object -First 1
    if (-not $Bundle) { $Bundle = $candidates[0] }
}

Write-Host "project : $Project"
Write-Host "output  : $Output"
Write-Host "bundle  : $Bundle"

Write-Host "[1/4] dotnet publish"
& dotnet publish $Project -c $Configuration -r win-x64 --self-contained true -o $Output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "[2/4] verify output"
$required = @('KiwixApple.exe', 'KiwixApple.dll', 'KiwixApple.runtimeconfig.json', 'av_libglesv2.dll')
foreach ($f in $required) {
    $p = Join-Path $Output $f
    if (-not (Test-Path -LiteralPath $p)) { throw "missing $f" }
    Write-Host ("      OK {0}  {1:N2} MB" -f $f, ((Get-Item $p).Length / 1MB))
}
$files = Get-ChildItem $Output -Recurse -File
Write-Host ("      {0} files, {1:N1} MB total" -f $files.Count, (($files | Measure-Object Length -Sum).Sum / 1MB))

# The self-test and the GUI test resolve the bundle by walking up from the
# executable, so the published folder has to sit inside a bundle for either of
# them to mean anything. Staged into the bundle, used there, and removed again.
$stage = Join-Path $Bundle 'app\gui\KiwixApple'
if (Test-Path $Bundle) {
    Write-Host "[3/4] headless self-test (staged into $stage)"
    Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item (Join-Path $Output '*') $stage -Recurse -Force
    try {
        $log = Join-Path $Root 'selftest.log'
        $err = Join-Path $Root 'selftest.err'
        $self = Start-Process (Join-Path $stage 'KiwixApple.exe') -ArgumentList '--selftest' `
                -WorkingDirectory $Bundle -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
        $null = $self.WaitForExit(600000)
        Get-Content $log -Encoding UTF8 | ForEach-Object { Write-Host "      $_" }
        if ((Get-Content $log -Raw -Encoding UTF8) -notmatch 'RESULT: PASS') {
            throw "self-test did not pass; see $log"
        }

        if ($SkipGuiTest) {
            Write-Host "[4/4] GUI end-to-end test skipped on request"
        } else {
            Write-Host "[4/4] GUI end-to-end test (presses the real buttons)"
            & (Join-Path $Root 'test-gui-start.ps1') -Exe (Join-Path $stage 'KiwixApple.exe') -Port 8092
            if ($LASTEXITCODE -ne 0) { throw "GUI end-to-end test failed" }
        }
    }
    finally {
        # Leave the bundle exactly as it was found: staging a 94 MB folder into
        # someone's U盘 and leaving it there is not a build side effect.
        Get-Process kiwix-serve -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "[3/4] no bundle at $Bundle, stopping after publish"
    if (-not $SkipSelfTest) { Write-Warning "self-test needs a bundle layout (zim\ and app\)" }
}

Write-Host ""
Write-Host "done."
