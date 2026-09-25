# ============================================================
#  From Kiwix official source: fetch static binaries (Windows)
#  Usage:  powershell -File scripts\fetch-kiwix-binaries.ps1 [version]
#  These binaries are NOT committed to git.
#  NOTE: keep this file ASCII-only. PowerShell 5.1 reads .ps1 as
#        ANSI when there is no BOM, which corrupts non-ASCII lines.
# ============================================================
param(
    [string]$Version = $(if ($env:KIWIX_VERSION) { $env:KIWIX_VERSION } else { '3.8.2' })
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root   = Split-Path -Parent $PSScriptRoot
$Bundle = if ($env:BUNDLE_DIR) { $env:BUNDLE_DIR } else { Join-Path $Root 'bundle' }
$Cache  = Join-Path $Root ".cache\kiwix-tools-$Version"
$Base   = 'https://download.kiwix.org/release/kiwix-tools'

New-Item -ItemType Directory -Force -Path (Join-Path $Bundle 'app') | Out-Null
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

# The official release index carries linux/mac archives. Windows
# binaries are looked up separately (they are not always built for
# every kiwix-tools release).
$Names = @(
    "kiwix-tools_win-i686-$Version.zip",
    "kiwix-tools_win-x86_64-$Version.zip"
)

$got = $false
foreach ($n in $Names) {
    $url = "$Base/$n"
    $out = Join-Path $Cache $n
    Write-Host "[1/3] try $n"
    try {
        if (-not (Test-Path -LiteralPath $out)) {
            Invoke-WebRequest $url -OutFile $out -UseBasicParsing -TimeoutSec 300
        }
        $dest = Join-Path $Bundle 'app\windows-x86_64'
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Expand-Archive -LiteralPath $out -DestinationPath "$Cache\extracted" -Force
        Get-ChildItem "$Cache\extracted" -Recurse -Filter 'kiwix-*.exe' -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dest $_.Name) -Force
        }
        Write-Host "      OK -> $dest"
        $got = $true
        break
    } catch {
        Write-Host "      not available for this version"
    }
}

if (-not $got) {
    Write-Warning "No official Windows build for kiwix-tools $Version."
    Write-Warning "Download a Windows build manually from $Base and copy"
    Write-Warning "kiwix-serve.exe / kiwix-manage.exe / kiwix-search.exe into"
    Write-Warning "$Bundle\app\windows-x86_64\"
    exit 0
}

Write-Host ""
Write-Host "Done. Contents of app\windows-x86_64:"
Get-ChildItem (Join-Path $Bundle 'app\windows-x86_64') |
    Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
