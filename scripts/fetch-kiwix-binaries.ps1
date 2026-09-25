# ============================================================
#  From Kiwix official source: fetch Windows static binaries
#  Usage:  powershell -File scripts\fetch-kiwix-binaries.ps1 [version]
#
#  NOTE: kiwix-tools does not publish every platform on every
#  release. For 3.8.2 there is no official Windows build, so this
#  script discovers the newest win-x86_64 archive that actually
#  exists instead of hardcoding a filename.
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
    Write-Warning "Could not read the official index at $Index"
    exit 0
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

Write-Host "[1/3] download $file"
if (-not (Test-Path -LiteralPath $out)) {
    Invoke-WebRequest "$Index$file" -OutFile $out -UseBasicParsing -TimeoutSec 600
} else { Write-Host "      cached" }

Write-Host "[2/3] verify official MD5"
$md5file = "$out.md5"
try {
    Invoke-WebRequest "$Index$file.md5" -OutFile $md5file -UseBasicParsing -TimeoutSec 120
    $expect = ((Get-Content $md5file -Raw) -split '\s+')[0]
    $actual = (Get-FileHash $out -Algorithm MD5).Hash
    if ($expect -ne $actual) { throw "MD5 mismatch: expected $expect got $actual" }
    Write-Host "      MD5 OK: $actual"
} catch {
    Write-Warning "MD5 check skipped/failed: $($_.Exception.Message)"
}

Write-Host "[3/3] extract -> $dest"
$ex = Join-Path $Cache 'extracted'
Remove-Item $ex -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $out -DestinationPath $ex -Force
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$n = 0
Get-ChildItem $ex -Recurse -Filter 'kiwix-*.exe' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dest $_.Name) -Force
    $n++
}
Write-Host "      $n executables"

if ($n -eq 0) { Write-Warning "no kiwix-*.exe found in $file" }

Get-ChildItem $dest | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
