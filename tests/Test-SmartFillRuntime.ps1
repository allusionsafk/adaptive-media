param([string]$Mpv = 'C:\mpv\mpv.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root '.artifacts\smart-fill-runtime'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$media = Join-Path $out 'cinema.mp4'
$result = Join-Path $out 'observed.json'
if (-not (Test-Path -LiteralPath $Mpv)) { throw "mpv unavailable: $Mpv" }
& ffmpeg -hide_banner -loglevel error -f lavfi -i 'testsrc2=size=960x400:rate=24' -t 4 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -y $media
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg fixture generation failed' }
Remove-Item -LiteralPath $result -ErrorAction SilentlyContinue
$env:DEMIMEDIA_SMART_FIT_PROBE = $result
$runtimeScript = '--script=' + (Join-Path $root 'payload\mpv-config\runtime\adaptive-playback.lua')
$probeScript = '--script=' + (Join-Path $PSScriptRoot 'SmartFitProbe.lua')
$logArg = '--log-file=' + (Join-Path $out 'mpv-diagnostic.log')
try {
    & $Mpv --no-config --audio=no --terminal=no --geometry=640x360 --keepaspect-window=no $runtimeScript $probeScript $logArg `
        --script-opts=adaptive-playback-fit=yes $media
} finally { Remove-Item Env:DEMIMEDIA_SMART_FIT_PROBE -ErrorAction SilentlyContinue }
for ($i = 0; $i -lt 100 -and -not (Test-Path -LiteralPath $result); $i++) { Start-Sleep -Milliseconds 100 }
if (-not (Test-Path -LiteralPath $result)) { throw 'Smart Fill probe did not report' }
$observed = Get-Content -Raw -LiteralPath $result | ConvertFrom-Json
if ($observed.fit.state -ne 'active' -or $observed.fit.samples -lt 1 -or $observed.zoom -le 0) {
    throw ('Smart Fill never analyzed and reframed a source frame: ' + (Get-Content -Raw -LiteralPath $result))
}
Write-Output "PASS: Smart Fill sampled $($observed.fit.samples) source frames; zoom=$($observed.zoom)"
