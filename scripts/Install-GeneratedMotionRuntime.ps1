#requires -Version 5.1
<#
Installs DemiMedia's experimental Generated Motion runtime into the app-private store:
  <data>\runtimes\generated-motion\<Id>\  (mpv.exe, FFmpeg with nvofruc, libraries, NvOFFRUC.dll)
and points current.json at it. The previous runtime folder is kept. Nothing outside
the data directory is modified.

Run with Windows PowerShell (powershell.exe). A Microsoft Store build of PowerShell 7
sees a virtualized AppData and would install where DemiMedia cannot see it.

  -RuntimeDirectory  output of runtimes\fruc-mpv\stage.sh
  -FrucDirectory     the Optical Flow SDK folder holding NvOFFRUC.dll and cudart64_110.dll
                     (NvOFFRUC\NvOFFRUCSample\bin\win64 in Optical_Flow_SDK_5.0.7.zip)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$FrucDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$Id,
    [string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
    $DataDirectory = if ($env:ADAPTIVE_MEDIA_DATA_DIR) { $env:ADAPTIVE_MEDIA_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'AdaptiveMediaPreview' }
}
$root = Join-Path $DataDirectory 'runtimes\generated-motion'
$target = Join-Path $root $Id
foreach ($required in @('mpv.exe', 'avfilter-12.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $RuntimeDirectory $required) -PathType Leaf)) { throw "Runtime directory lacks $required." }
}
foreach ($required in @('NvOFFRUC.dll', 'cudart64_110.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $FrucDirectory $required) -PathType Leaf)) { throw "FRUC directory lacks $required." }
}
if (Test-Path -LiteralPath $target) { throw "Runtime $Id is already installed at $target; choose a new Id." }

$staging = "$target.incoming"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Get-ChildItem -LiteralPath $RuntimeDirectory -File | Where-Object Name -NotIn @('SHA256SUMS') | Copy-Item -Destination $staging
Copy-Item -LiteralPath (Join-Path $FrucDirectory 'NvOFFRUC.dll'), (Join-Path $FrucDirectory 'cudart64_110.dll') -Destination $staging

$files = [ordered]@{}
Get-ChildItem -LiteralPath $staging -File | Sort-Object Name | ForEach-Object { $files[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$version = & (Join-Path $staging 'mpv.com') --no-config --version 2>$null | Select-Object -First 1
$manifest = [ordered]@{
    schemaVersion = 1
    id            = $Id
    description   = "Experimental Generated Motion runtime: $version; FFmpeg 9.0.2 with nvofruc; NVIDIA Optical Flow SDK 5.0 FRUC"
    installedUtc  = (Get-Date).ToUniversalTime().ToString('o')
    files         = $files
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding UTF8

# Self-test the staged player before it can become current. A software test source
# cannot negotiate the filter's D3D11-only input, so lavfi reports a conversion
# failure after resolving the filter; a build without it says "No such filter".
$selfTest = & (Join-Path $staging 'mpv.com') --no-config --vo=null --ao=null --frames=2 --msg-level=all=v `
    '--vf=lavfi=[nvofruc]' 'av://lavfi:testsrc=size=64x64:rate=24' 2>&1 | Out-String
if ($selfTest -match 'No such filter' -or $selfTest -notmatch 'Impossible to convert between the formats') {
    Remove-Item -LiteralPath $staging -Recurse -Force
    throw "The staged runtime does not provide the nvofruc filter.`n$selfTest"
}

Move-Item -LiteralPath $staging -Destination $target
$pointer = Join-Path $root 'current.json'
[ordered]@{ id = $Id } | ConvertTo-Json | Set-Content -LiteralPath "$pointer.tmp" -Encoding UTF8
Move-Item -LiteralPath "$pointer.tmp" -Destination $pointer -Force
Write-Host "Installed Generated Motion runtime $Id at $target ($($files.Count) files)."
