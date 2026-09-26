# ============================================================
#  From Kiwix official source: fetch Windows binaries
#  Usage:  powershell -File scripts\fetch-kiwix-binaries.ps1 [version]
#
#  NOTE: kiwix-tools does not publish every platform on every
#  release. For 3.8.2 there is no official Windows build, so this
#  script discovers the newest win-x86_64 archive that actually
#  exists instead of hardcoding a filename.
#
#  NOTE: the WHOLE extracted tree is copied, not just kiwix-*.exe.
#        The 3.8.1 Windows build is not statically linked: its
#        executables are ~3.7 MB and need the sibling DLLs from
#        the same archive. Copying only the exes produced a bundle
#        whose kiwix-serve.exe died instantly with
#        STATUS_DLL_NOT_FOUND (0xC0000135), and nothing caught it
#        because the launcher starts fine and only fails when the
#        user presses "start service". An older self-contained
#        build (3.7.0) is ~10 MB per exe for comparison.
#
#  NOTE: keep this file ASCII-only. PowerShell 5.1 reads a BOM-less
#        .ps1 as ANSI, which corrupts non-ASCII lines and silently
#        eats variables.
# ============================================================
param(
    [string]$Version = $(if ($env:KIWIX_VERSION) { $env:KIWIX_VERSION } else { '3.8.2' })
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $PSScriptRoot
$Bundle = if ($env:BUNDLE_DIR) { $env:BUNDLE_DIR } else { Join-Path $Root 'bundle' }
$Cache  = Join-Path $Root '.cache'
$Index  = 'https://download.kiwix.org/release/kiwix-tools/'

New-Item -ItemType Directory -Force -Path (Join-Path $Bundle 'app') | Out-Null
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

function Get-AvailableWindowsBuild {
    $html = (Invoke-WebRequest $Index -UseBasicParsing -TimeoutSec 120).Content
    $rx = [regex]'kiwix-tools_win-x86_64-([0-9]+(?:\.[0-9]+)+)\.zip'
    $all = @{}
    foreach ($m in $rx.Matches($html)) { $all[$m.Groups[1].Value] = $true }
    if ($all.Count -eq 0) { return $null }
    # newest first, numeric-aware
    $sorted = $all.Keys | Sort-Object { [version]$_ } -Descending
    return @($sorted)
}

Write-Host "requested version: $Version"
$available = Get-AvailableWindowsBuild
if (-not $available) {
    throw "Could not read the official index at $Index"
}
Write-Host ("official win-x86_64 builds: {0}" -f ($available -join ', '))

$pick = $null
if ($available -contains $Version) { $pick = $Version }
else { $pick = $available[0] }

if ($pick -ne $Version) {
    Write-Warning "No Windows build for kiwix-tools $Version; falling back to $pick"
    Write-Warning "(This is normal: upstream ships platforms on separate schedules.)"
}

$file = "kiwix-tools_win-x86_64-$pick.zip"
$out  = Join-Path $Cache $file
$dest = Join-Path $Bundle 'app\windows-x86_64'

Write-Host "[1/4] download $file"
if (-not (Test-Path -LiteralPath $out)) {
    Invoke-WebRequest "$Index$file" -OutFile $out -UseBasicParsing -TimeoutSec 600
} else { Write-Host "      cached" }

Write-Host "[2/4] verify official MD5"
$md5file = "$out.md5"
try {
    Invoke-WebRequest "$Index$file.md5" -OutFile $md5file -UseBasicParsing -TimeoutSec 120
    $expect = ((Get-Content $md5file -Raw) -split '\s+')[0]
    $actual = (Get-FileHash $out -Algorithm MD5).Hash
    if ($expect -ne $actual) { throw "MD5 mismatch: expected $expect got $actual" }
    Write-Host "      MD5 OK: $actual"
} catch {
    throw "MD5 check failed: $($_.Exception.Message)"
}

Write-Host "[3/4] extract -> $dest"
$ex = Join-Path $Cache 'extracted'
Remove-Item $ex -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $out -DestinationPath $ex -Force

# Copy the whole tree. The DLLs are not optional: see the note at the top.
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Path (Join-Path $ex '*') -Destination $dest -Recurse -Force

$exes = @(Get-ChildItem $dest -Recurse -Filter 'kiwix-*.exe' -File)
$dlls = @(Get-ChildItem $dest -Recurse -Filter '*.dll'     -File)
Write-Host ("      {0} executables, {1} DLLs" -f $exes.Count, $dlls.Count)
if ($exes.Count -eq 0) { throw "no kiwix-*.exe found in $file" }
$serve = Join-Path $dest 'kiwix-serve.exe'
if (-not (Test-Path -LiteralPath $serve)) { throw "kiwix-serve.exe missing from $dest" }

Write-Host "[4/4] verify the binary actually runs"
# Only possible on a Windows host. The release job used to fetch these on
# ubuntu through pwsh, which is exactly how a broken binary reached a
# published release: nothing could execute it to find out.
$isWin = $true
if (Get-Command pwsh -ErrorAction SilentlyContinue) {
    # pwsh on Linux: $IsWindows is available in PS Core.
    try { $isWin = [bool](& pwsh -NoProfile -Command '$IsWindows') } catch { $isWin = $false }
}
if ($isWin) {
    $ver = & $serve '--version' 2>&1 | Out-String
    $code = $LASTEXITCODE
    Write-Host ("      exit={0} {1}" -f $code, $ver.Trim())
    if ($code -ne 0) {
        # 0xC0000135 surfaces as a negative/huge unsigned exit code here.
        throw "kiwix-serve.exe --version failed with exit code $code (a non-self-contained build was probably extracted without its DLLs)"
    }
    if ($ver -notmatch 'kiwix-tools') {
        throw "kiwix-serve.exe --version did not report a version: '$($ver.Trim())'"
    }
} else {
    Write-Warning "not running on Windows, cannot execute kiwix-serve.exe to verify it"
}

Get-ChildItem $dest -Recurse -File |
    Select-Object @{n = 'File'; e = { $_.FullName.Substring($dest.Length + 1) } },
                  @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize
