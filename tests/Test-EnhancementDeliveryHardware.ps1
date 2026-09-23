#requires -Version 7.0
# Real-hardware enhancement delivery validation (NVIDIA RTX + real mpv). Not part
# of the portable validation gate: it needs the GPU, a display and an mpv build.
param([string]$Mpv = 'C:\mpv\mpv.exe',
      [string]$OutputRoot = (Join-Path $PSScriptRoot '..\.artifacts\delivery-hardware'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $run -Force | Out-Null
$adapter = (Get-CimInstance Win32_VideoController | Where-Object Name -Match 'NVIDIA' | Select-Object -First 1).Name
if (-not $adapter) { throw 'No NVIDIA adapter is present; this validation needs one.' }
# A 960x540 25 fps 8-bit SDR source: upscaled into the window, and a cadence that
# does not map cleanly onto a 240 Hz panel. A smooth gradient gives deband work;
# the audio track is silent.
$media = Join-Path $run 'sdr540p25.mkv'
& ffmpeg -hide_banner -loglevel error -f lavfi -i 'gradients=size=960x540:rate=25:speed=0.02,format=yuv420p' `
    -f lavfi -i 'testsrc2=size=320x180:rate=25' -f lavfi -i 'anullsrc=r=48000:cl=stereo' `
    -filter_complex '[0][1]overlay=40:40:shortest=1[v]' -map '[v]' -map 2 -t 40 -c:v libx264 -preset veryfast -crf 18 `
    -pix_fmt yuv420p -c:a aac -y $media
if ($LASTEXITCODE -ne 0) { throw 'Test media could not be generated.' }
dotnet build (Join-Path $root 'tests/DeliveryHardwareValidation/DeliveryHardwareValidation.csproj') -c Release -v q -o (Join-Path $run 'bin')
if ($LASTEXITCODE -ne 0) { throw 'Validation harness build failed.' }
& (Join-Path $run 'bin/DeliveryHardwareValidation.exe') $Mpv $media $run $adapter
$code = $LASTEXITCODE
Write-Host "Report: $run\delivery-hardware.json"
exit $code
