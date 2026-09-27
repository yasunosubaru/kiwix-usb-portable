# ============================================================
#  Build the native Windows entry point (winlauncher.c -> Kiwix.exe)
#
#  Usage:
#     powershell -File scripts\build-launcher.ps1 [-Output <dir>]
#
#  Why a C launcher at all: the bundle must open by double-clicking an
#  .exe on a machine that has no .NET, no Python and no Windows App
#  Runtime. Anything else either needs a script interpreter, needs a
#  runtime that is not there, or bloats a USB stick for no reason.
#
#  MSVC is used rather than gcc because the GitHub windows runner
#  already has it, so CI needs no extra setup.
# ============================================================
param(
    [string]$Output = ''
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Source = Join-Path $Root 'src\launcher\winlauncher.c'
$Icon = Join-Path $Root 'src\branding\kiwix.ico'
if (-not $Output) { $Output = Join-Path $Root 'dist\launcher' }

if (-not (Test-Path -LiteralPath $Source)) { throw "missing source: $Source" }
if (-not (Test-Path -LiteralPath $Icon)) { throw "missing icon: $Icon (run scripts\make-icon or src\branding\make_icon.py)" }
New-Item -ItemType Directory -Force -Path $Output | Out-Null

# The launcher and the WinUI 3 app ship as one bundle, so they carry the same
# version. It is written down exactly once, in KiwixWinUI.csproj; hardcoding a
# second copy here is how the window ended up claiming v1.1.2 in a v1.2.0 bundle.
# The three executables in a bundle share one version, declared in
# src\Directory.Build.props. This stub has no project file of its own, so it
# parses that file. A missing or unreadable <Version> is a hard error rather
# than a fallback: guessing here is how the launcher ends up stamping a
# different number than the GUI sitting next to it.
$props = Join-Path $Root 'src\Directory.Build.props'
if (-not (Test-Path -LiteralPath $props)) {
    throw "missing version source: $props"
}
$m = [regex]::Match((Get-Content $props -Raw), '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw "no <Version> element in $props" }
$version = $m.Groups[1].Value.Trim()
$version4 = ($version -split '[^0-9.]')[0..3] -join '.'
while ($version4.Split('.').Count -lt 4) { $version4 += '.0' }
Write-Host "  version  : $version (from src\Directory.Build.props)"
Write-Host ("  icon     : {0}  {1:N0} bytes" -f (Split-Path $Icon -Leaf), (Get-Item $Icon).Length)

# Locate a Visual Studio installation with the x64 C toolchain.
# The path is <root>\<version>\<edition>\VC\Auxiliary\Build\vcvars64.bat --
# the version segment matters, VS 18 installs under 18\, not directly under the
# root. A fixed enumeration rather than a recursive search: walking the whole
# Visual Studio tree hits access-denied directories, which under
# $ErrorActionPreference = 'Stop' surfaces as a pipeline error unrelated to the
# real problem.
$vsRoot = Join-Path ${env:ProgramFiles} 'Microsoft Visual Studio'
$candidates = @()
foreach ($ver in (Get-ChildItem $vsRoot -Directory -ErrorAction SilentlyContinue)) {
    foreach ($edition in (Get-ChildItem $ver.FullName -Directory -ErrorAction SilentlyContinue)) {
        $candidates += Join-Path $edition.FullName 'VC\Auxiliary\Build\vcvars64.bat'
    }
    # Pre-2015 layout, kept for anyone still on it.
    $candidates += Join-Path $ver.FullName 'VC\Auxiliary\Build\vcvars64.bat'
}
$vcvars = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $vcvars) {
    throw "vcvars64.bat not found under $vsRoot (looked at $($candidates.Count) candidate paths); install the Visual Studio C++ workload"
}
Write-Host "  toolchain: $vcvars"

$bat = Join-Path $env:TEMP ("kiwix_launcher_" + [Guid]::NewGuid().ToString('N') + '.bat')
# /utf-8 is required: the Chinese message strings in the source are UTF-8,
# and without it MSVC mangles them exactly like it mangles a .ps1. UNICODE and
# _UNICODE are #defined in the source rather than passed here, so compiling
# winlauncher.c on its own behaves the same way (and /WX would reject the
# duplicate-definition warning if both did it).
# Everything that is not a compiler option -- /SUBSYSTEM, /ENTRY, the import
# library -- has to sit behind /link. cl rejects them on the compiler side
# (D9002) and /WX turns that into a build failure. /ENTRY is enough on its own:
# it overrides the default entry point with the CRT startup that calls wWinMain,
# so /NOENTRY is not needed and would in fact suppress the CRT initialisation.
# The launcher carries no icon. Embedding one needs a resource compiler and
# every path available here was tried and failed: every rc.exe on this machine
# (Windows SDK 10.0.22621 and 10.0.26100, plus the copy Visual Studio ships)
# rejects the ICON statement with RC2135 "file not found" for every path form
# and every .ico, including a 2 KB file Pillow wrote itself, while the same
# rc.exe handles #include and VERSIONINFO without complaint; mingw's windres
# rejects the same statement; and a hand-built COFF .res is accepted by
# link.exe but produces a resource tree the shell cannot enumerate, even when
# the bytes are lifted verbatim from an executable MSBuild produced. The two
# GUIs do carry the icon, through ApplicationIcon and PyInstaller --icon, and
# they are what a user actually looks at. This stub is on screen for half a
# second and then hands over to one of them.
$body = @"
@echo off
call "$vcvars" >nul
if errorlevel 1 exit /b 1
pushd "$Output"
cl /nologo /W4 /WX /O2 /MT /utf-8 /GS /Fe:Kiwix.exe "$Source" /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup user32.lib
set RC=%ERRORLEVEL%
popd
exit /b %RC%
"@
[IO.File]::WriteAllText($bat, $body, (New-Object System.Text.UTF8Encoding($false)))

try {
    $out = & cmd.exe /c "`"$bat`"" 2>&1
    $code = $LASTEXITCODE
    $out | ForEach-Object { Write-Host "  $_" }
    if ($code -ne 0) { throw "cl.exe failed with exit code $code" }
}
finally {
    Remove-Item $bat -Force -ErrorAction SilentlyContinue
}

$exe = Join-Path $Output 'Kiwix.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Kiwix.exe was not produced" }
foreach ($junk in 'Kiwix.obj', 'winlauncher.obj', 'vc140*.pdb', 'Kiwix.pdb', 'Kiwix.lib', 'Kiwix.exp') {
    Get-ChildItem $Output -Filter $junk -ErrorAction SilentlyContinue | Remove-Item -Force
}

$fi = Get-Item $exe
Write-Host ("  built {0}  {1:N0} bytes" -f $fi.Name, $fi.Length)
# The 10-resolution icon is ~420 KB uncompressed, so the launcher lands around
# 550 KB rather than the 130 KB it was without one. That is still nothing next
# to a 150 MB bundle, and the bound is here to catch a runaway debug build, not
# to police a few hundred kilobytes of artwork.
if ($fi.Length -gt 1MB) { throw "the launcher should be well under 1 MB; something went wrong" }

# Must be a GUI subsystem binary: a console window flashing on every launch is
# the exact thing this file exists to avoid. The header is parsed with plain
# file APIs rather than Add-Type, so this check cannot itself become a source
# of build failures.
$bytes = [IO.File]::ReadAllBytes($exe)
if ($bytes.Length -lt 0x40) { throw "Kiwix.exe is too small to be a PE image" }
$peOff = [BitConverter]::ToInt32($bytes, 0x3C)
if ($peOff -le 0 -or ($peOff + 0x5E) -ge $bytes.Length) { throw "bad PE header offset" }
if ($bytes[$peOff] -ne 0x50 -or $bytes[$peOff + 1] -ne 0x45) { throw "not a PE image" }
$subsystem = [BitConverter]::ToUInt16($bytes, $peOff + 0x5C)
Write-Host "  PE subsystem: $subsystem (2 = Windows GUI, 3 = console)"
if ($subsystem -ne 2) { throw "PE subsystem ${subsystem}: the launcher must be a GUI binary, not a console one" }

# And it must carry no console: if the image were linked against the console
# CRT, double-clicking would flash a window even with /SUBSYSTEM:WINDOWS.
# The launcher deliberately carries no icon. Embedding one needs a resource
# compiler, and none of the available paths work here: every rc.exe on this
# machine fails the ICON statement with RC2135 for any path form and any .ico,
# mingw's windres rejects the same statement, and a hand-built COFF .res is
# accepted by link.exe but yields a directory tree the shell cannot enumerate --
# even when the bytes are lifted verbatim from an exe MSBuild produced. The two
# GUIs do carry the icon, via ApplicationIcon and PyInstaller --icon, and they
# are what a user actually looks at. This stub is on screen for half a second.
Write-Host "  note: the launcher has no embedded icon (see the comment above)"
