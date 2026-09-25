# ============================================================
#  Convert a Docker OCI-layout image tar into legacy docker-archive.
#
#  WHY: Docker 25+ `docker save` writes an OCI layout (layers under
#  blobs/sha256/, plus oci-layout + index.json). Older importers -
#  e.g. the one behind the Synology/fnOS "import image" button -
#  only accept the legacy layout (<hash>/layer.tar + manifest.json)
#  and fail with a generic parameter error.
#
#  Usage:  powershell -File scripts\make-docker-legacy-tar.ps1 -Image ghcr.io/kiwix/kiwix-serve:latest -Output kiwix-serve-legacy.tar
#
#  ASCII-only on purpose (PowerShell 5.1 reads BOM-less .ps1 as ANSI).
# ============================================================
param(
    [string]$Image = 'ghcr.io/kiwix/kiwix-serve:latest',
    [string]$Output = 'kiwix-serve-legacy.tar',
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'

if (-not $WorkDir) { $WorkDir = Join-Path $env:TEMP ("legacy_" + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
$Output = if ([IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path (Get-Location) $Output }

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$oci = Join-Path $WorkDir 'image.tar'
$legacy = Join-Path $WorkDir 'legacy'

Write-Host "[1/5] docker save -> $oci"
& docker save $Image -o $oci
if ($LASTEXITCODE -ne 0) { throw "docker save failed" }

Write-Host "[2/5] extract"
& tar -xf $oci -C $WorkDir
if ($LASTEXITCODE -ne 0) { throw "extract failed" }

$m = (Get-Content (Join-Path $WorkDir 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json)[0]
$configHash = Split-Path $m.Config -Leaf
$layerRels = @($m.Layers)
$tag = @($m.RepoTags)[0]

Write-Host "      image    : $tag"
Write-Host "      layers   : $($layerRels.Count)"

Write-Host "[3/5] write legacy layout"
New-Item -ItemType Directory -Force -Path $legacy | Out-Null
Copy-Item (Join-Path $WorkDir $m.Config) (Join-Path $legacy "$configHash.json") -Force

$layerFiles = New-Object System.Collections.ArrayList
foreach ($rel in $layerRels) {
    $h = Split-Path $rel -Leaf
    $d = Join-Path $legacy $h
    New-Item -ItemType Directory -Force -Path $d | Out-Null
    Copy-Item (Join-Path $WorkDir $rel) (Join-Path $d 'layer.tar') -Force
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText((Join-Path $d 'VERSION'), '1.0', $utf8)
    [IO.File]::WriteAllText((Join-Path $d 'json'), '{}', $utf8)
    [void]$layerFiles.Add("$h/layer.tar")
}

# NOTE: ConvertTo-Json on a piped single-element array collapses it to an
# object, which `docker load` rejects with "cannot unmarshal object into
# []tarexport.manifestItem". Always pass the array via -InputObject.
$obj = [pscustomobject]@{
    Config   = "$configHash.json"
    RepoTags = @($tag)
    Layers   = @($layerFiles.ToArray())
}
$json = ConvertTo-Json -InputObject @($obj) -Depth 6 -Compress
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $legacy 'manifest.json'), $json, $utf8)

$repos = @{}
$repos[(($tag -split ':')[0])] = @{ latest = (Split-Path $layerRels[-1] -Leaf) }
[IO.File]::WriteAllText((Join-Path $legacy 'repositories'), ($repos | ConvertTo-Json -Depth 5 -Compress), $utf8)

Write-Host "[4/5] pack -> $Output"
Remove-Item $Output -Force -ErrorAction SilentlyContinue
$entries = @(Get-ChildItem $legacy | Select-Object -ExpandProperty Name)
& tar -cf $Output -C $legacy @entries
if ($LASTEXITCODE -ne 0) { throw "pack failed" }

Write-Host "[5/5] verify with docker load"
& docker rmi $Image 2>&1 | Out-Null
& docker load -i $Output
if ($LASTEXITCODE -ne 0) { throw "docker load verification failed" }

$f = Get-Item $Output
Write-Host ""
Write-Host ("OK: {0}  ({1:N1} MB)" -f $f.FullName, ($f.Length / 1MB))
Write-Host ("SHA256: {0}" -f (Get-FileHash $Output -Algorithm SHA256).Hash)
Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
