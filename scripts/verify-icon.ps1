# ============================================================
#  Fail the build when an executable carries no icon.
#
#  Usage:
#     powershell -File scripts\verify-icon.ps1 -Exe <path> [-AllowMissing]
#
#  Why this exists
#  --------------
#  A missing icon is a silent failure. Nothing errors, the build goes green,
#  and the only symptom is a blank page in Explorer and on the taskbar. The
#  Windows GUI in 1.1.x shipped with no ApplicationIcon at all and nothing
#  noticed, so this turns the symptom into a build failure.
#
#  The check goes through ExtractIconEx, i.e. the same shell32 path Explorer
#  uses, rather than trusting that a build setting was applied.
# ============================================================
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [switch]$AllowMissing
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "not found: $Exe" }

Add-Type -Namespace VI -Name S -MemberDefinition @"
[DllImport("shell32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
public static extern int ExtractIconExW(string f, int idx, IntPtr[] large, IntPtr[] small, uint n);
[DllImport("user32.dll", SetLastError=true)]
public static extern bool DestroyIcon(IntPtr h);
"@

$groups = [VI.S]::ExtractIconExW((Resolve-Path $Exe).Path, -1, $null, $null, 0)
Write-Host "  $Exe : $groups icon group(s)"

if ($groups -le 0) {
    if ($AllowMissing) {
        Write-Warning "no icon, permitted by -AllowMissing"
        exit 0
    }
    Write-Error "::error::no icon embedded. Windows will show a blank page for this file."
    exit 1
}

# Materialise it as well: a group that cannot be turned into a handle is as good
# as no icon at all, and that is exactly the failure mode of a malformed
# resource section.
$large = New-Object IntPtr[] 1
$small = New-Object IntPtr[] 1
$got = [VI.S]::ExtractIconExW((Resolve-Path $Exe).Path, 0, $large, $small, 1)
if ($got -le 0 -or $large[0] -eq [IntPtr]::Zero) {
    Write-Error "::error::an icon group is present but Windows cannot render it"
    exit 1
}
[void][VI.S]::DestroyIcon($large[0])
Write-Host "  OK: icon present and renderable"
exit 0
