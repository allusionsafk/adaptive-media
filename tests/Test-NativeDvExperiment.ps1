#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $root 'scripts\NativeDvExperiment.psm1'

$script:assertions = 0

function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:assertions++
    if (-not $Condition) { throw "ASSERT: $Message" }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    $script:assertions++
    if ($Expected -ne $Actual) {
        throw "ASSERT: $Message (expected '$Expected', actual '$Actual')"
    }
}

Import-Module $modulePath -Force

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("adaptive-media-native-dv-test-" + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempRoot)
try {
    $runtimeDir = Join-Path $tempRoot 'runtime'
    $runDir = Join-Path $tempRoot 'run'
    $manifestDir = Join-Path $tempRoot 'manifest'
    [void][IO.Directory]::CreateDirectory($runtimeDir)
    [void][IO.Directory]::CreateDirectory($manifestDir)

    $runtimePath = Join-Path $runtimeDir 'mpv.com'
    [IO.File]::WriteAllBytes($runtimePath, [byte[]](0x4d, 0x5a))
    $runtimeHash = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $relativeRuntime = '../runtime/mpv.com'
    $manifestPath = Join-Path $manifestDir 'runtime-manifest.json'
    @{
        schemaVersion = 1
        runtime = @{
            consoleLauncher = @{
                pathRelativeToManifest = $relativeRuntime
                bytes = 2
                sha256 = $runtimeHash
            }
        }
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

    $sourcePath = Join-Path $tempRoot 'authored [profile 7] source.mkv'
    [IO.File]::WriteAllBytes($sourcePath, [byte[]](0..255))

    $on = New-NativeDvInvocation -RuntimeManifest $manifestPath -SourcePath $sourcePath `
        -RunDirectory $runDir -EnhancementLayer $true -StartSeconds 123.5 -DurationSeconds 30 `
        -PipeName 'native-dv-test-pipe'
    $off = New-NativeDvInvocation -RuntimeManifest $manifestPath -SourcePath $sourcePath `
        -RunDirectory $runDir -EnhancementLayer $false -StartSeconds 123.5 -DurationSeconds 30 `
        -PipeName 'native-dv-test-pipe-off'

    Assert-True ([IO.Path]::IsPathRooted($on.Executable)) 'runtime executable must be absolute'
    Assert-Equal ([IO.Path]::GetFullPath($runtimePath)) $on.Executable 'manifest runtime must be used exactly'
    Assert-True (-not $on.Executable.StartsWith('C:\mpv\', [StringComparison]::OrdinalIgnoreCase)) 'stable mpv must not be selected'
    Assert-True ($on.Arguments -contains '--no-config') 'no-config is mandatory'
    Assert-True ($on.Arguments -contains '--vo=gpu-next') 'gpu-next is mandatory'
    Assert-True ($on.Arguments -contains '--hwdec=auto-safe') 'hardware decoder request must be explicit'
    Assert-True ($on.Arguments -contains '--cache-on-disk=no') 'disk cache must be disabled'
    Assert-True ($on.Arguments -contains '--gpu-shader-cache=no') 'shader cache must be disabled'
    Assert-True ($on.Arguments -contains '--save-position-on-quit=no') 'watch-later writes must be disabled'
    Assert-True ($on.Arguments -contains '--vf=format=enhancement-layer=yes') 'FEL-on must be explicit'
    Assert-True ($off.Arguments -contains '--vf=format=enhancement-layer=no') 'FEL-off must be explicit'
    Assert-True (@($on.Arguments | Where-Object { $_ -like '--input-ipc-server=*native-dv-test-pipe' }).Count -eq 1) 'isolated IPC endpoint is mandatory'
    Assert-True (@($on.Arguments | Where-Object { $_ -like '--config-dir=*' }).Count -eq 1) 'isolated config path is mandatory'
    Assert-True (@($on.Arguments | Where-Object { $_ -like '--demuxer-cache-dir=*' }).Count -eq 1) 'isolated demux cache path is mandatory'
    Assert-True (@($on.Arguments | Where-Object { $_ -like '--log-file=*' }).Count -eq 1) 'isolated log path is mandatory'
    Assert-True ([IO.Path]::IsPathRooted($on.Environment.TEMP)) 'TEMP must be absolute'
    Assert-True ($on.Environment.TEMP.StartsWith([IO.Path]::GetFullPath($runDir), [StringComparison]::OrdinalIgnoreCase)) 'TEMP must be inside the run'
    Assert-Equal $on.Environment.TEMP $on.Environment.TMP 'TEMP and TMP must share the isolated location'
    $delimiter = [Array]::IndexOf([string[]]$on.Arguments, '--')
    Assert-True ($delimiter -ge 0) 'end-of-options delimiter is mandatory'
    Assert-Equal ([IO.Path]::GetFullPath($sourcePath)) $on.Arguments[$delimiter + 1] 'source path must be unchanged after delimiter'
    Assert-Equal ($delimiter + 2) $on.Arguments.Count 'source must be the final argument'
    Assert-True (-not (($on.Arguments -join "`n") -match '(?i)(extract|encode|stream-record|record-file|dump-stream|--o=|--output=)')) 'invocation must not contain extraction or output arguments'

    $felLog = @'
[mkv] Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track).
[vd] Opening decoder hevc
[vd] Selected decoder: hevc - HEVC
[vd] Opening decoder hevc
[vd] Selected decoder: hevc - HEVC
[vf] [el_pair] 3840x2160 yuv420p10 dolbyvision/bt.2020/pq/limited/display
[vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)
[vo/gpu-next/libplacebo] [285] /* sh_dovi_compose_nlq */
[vo/gpu-next/libplacebo] Spent 1.261 ms on shader: color decoding, enhancement layer
'@
    $runtimeEvidence = Get-NativeDvRuntimeEvidence -LogText $felLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'nvdec'
    Assert-True $runtimeEvidence.Profile7Splitter 'splitter evidence must be structured'
    Assert-Equal 2 $runtimeEvidence.HevcDecoderOpenCount 'both decoder instances must be counted'
    Assert-True $runtimeEvidence.ElPair 'pairing evidence must be structured'
    Assert-True $runtimeEvidence.NlqCompositionShader 'NLQ shader evidence must be structured'
    Assert-True $runtimeEvidence.EnhancementLayerGpuStage 'GPU enhancement stage must be structured'
    Assert-Equal 371 $runtimeEvidence.LibplaceboApi 'renderer API must be parsed'
    $felClassification = Get-NativeDvMelFelClassification -DoviToolSummary 'Profile: 7 (FEL)' -RuntimeEvidence $runtimeEvidence
    Assert-Equal 'FEL' $felClassification.MelFel 'independent FEL evidence must classify FEL'
    Assert-True ($felClassification.Evidence.Count -ge 2) 'FEL classification must preserve independent evidence'
    $unknownEvidence = Get-NativeDvRuntimeEvidence -LogText '[mkv] profile: 7, EL: 1, BL: 1' -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'nvdec'
    $unknownClassification = Get-NativeDvMelFelClassification -DoviToolSummary 'Profile: 7' -RuntimeEvidence $unknownEvidence
    Assert-Equal 'Unknown' $unknownClassification.MelFel 'EL presence alone must not manufacture FEL'
    $melClassification = Get-NativeDvMelFelClassification -DoviToolSummary 'Profile: 7 (MEL)' -RuntimeEvidence $unknownEvidence
    Assert-Equal 'MEL' $melClassification.MelFel 'documented MEL summary must classify MEL'

    # --- Renderer diagnostics must not manufacture failures -------------------
    # gpu-next logs '[vo/gpu-next] Loading failed.' when it declines an optional
    # hwdec interop driver after another already succeeded. Real proof runs emit it
    # three times while hardware decoding works perfectly, so treating it as a
    # device-creation failure reports a broken renderer on a healthy run.
    $benignInteropLog = @'
[   0.285][v][vd] Looking at hwdec hevc-d3d11va...
[   0.285][v][vo/gpu-next] Loading hwdec drivers for format: 'd3d11'
[   0.285][v][vo/gpu-next] Loading hwdec driver 'd3d11va'
[   0.286][v][vo/gpu-next] Loading hwdec driver 'd3d11-egl'
[   0.286][v][vo/gpu-next] Loading failed.
[   0.318][i][vd] Using hardware decoding (d3d11va).
[   0.280][v][vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)
'@
    $benign = Get-NativeDvRuntimeEvidence -LogText $benignInteropLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    Assert-True (-not $benign.RendererDeviceFailureObserved) 'declining an optional hwdec interop driver is not a device failure'
    Assert-Equal 1 $benign.HwdecInteropProbeFailures 'the benign interop probe result must still be counted, not discarded'
    Assert-Equal 'd3d11va' $benign.HardwareDecoderInUse 'the hardware decoder actually in use must be parsed'
    Assert-True $benign.HardwareDecodingObserved 'hardware decoding must be observed on this log'

    $realFailureLog = '[vo/gpu-next] Could not create device.'
    $realFailure = Get-NativeDvRuntimeEvidence -LogText $realFailureLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    Assert-True $realFailure.RendererDeviceFailureObserved 'a genuine device-creation failure must still be reported'

    # --- Observed state must never be inferred from the request ----------------
    $requestedButNotComposed = Get-NativeDvRuntimeEvidence -LogText $benignInteropLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    Assert-True (-not $requestedButNotComposed.CompositionObserved) 'requesting the enhancement layer must not imply composition'
    $composedLog = $felLog + "`n" + '[vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)'
    $composedNotRequested = Get-NativeDvRuntimeEvidence -LogText $composedLog -RequestedEnhancementLayer $false -RequestedHardwareDecoder 'd3d11va'
    Assert-True $composedNotRequested.CompositionObserved 'observed composition must be reported even when it was not requested'

    # --- Tri-state pipeline state ---------------------------------------------
    $fakeInvocation = [pscustomobject]@{
        SourcePath = 'C:\authored\source.mkv'; Executable = 'C:\pinned\mpv.com'; EnhancementLayer = $true
        GpuApi = 'd3d11'; GpuContext = 'd3d11'; HardwareDecoder = 'd3d11va'
    }
    $silentEvidence = Get-NativeDvRuntimeEvidence -LogText '' -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    $silentState = Get-NativeDvPipelineState -RuntimeEvidence $silentEvidence -Invocation $fakeInvocation
    Assert-Equal 'Unknown' $silentState.Observed.FelComposition 'an absent renderer log must stay Unknown, never Inactive'
    Assert-Equal 'Unknown' $silentState.Observed.BlElPairing 'pairing must stay Unknown when the renderer never ran'
    Assert-Equal 'Unknown' $silentState.Observed.HardwareSurfaces 'hardware-surface state must stay Unknown without evidence'
    Assert-Equal 'Unknown' $silentState.Observed.RpuState 'RPU state must stay Unknown without splitter evidence'
    Assert-True $silentState.Requested.EnhancementLayer 'the request must still be recorded when nothing was observed'

    $onEvidence = Get-NativeDvRuntimeEvidence -LogText $composedLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    $onState = Get-NativeDvPipelineState -RuntimeEvidence $onEvidence -Invocation $fakeInvocation -SourceClassification 'FEL'
    Assert-Equal 'Active' $onState.Observed.FelComposition 'observed NLQ composition must report Active'
    Assert-Equal 'Active' $onState.Observed.BlElPairing 'observed el_pair must report Active pairing'
    Assert-Equal 'Present' $onState.Observed.RpuState 'the Profile 7 splitter establishes RPU presence'
    Assert-Equal 2 $onState.Observed.DecoderInstances 'both decoder instances must reach the observed model'
    Assert-True ($null -ne $onState.Observed.ElDecoder) 'the enhancement-layer decoder must be named'
    Assert-Equal 0 $onState.Observed.Degradation.Count 'a fully composed run must report no degradation'

    # A requested FEL run that the renderer did not compose is a base-layer-only
    # result and must say so rather than inheriting the FEL label from the request.
    $blOnlyLog = @'
[   0.016][v][mkv] Dolby Vision Profile 7 splitter: BL stream 0, virtual EL stream 1 (dependent_track).
[   0.285][v][vd] Opening decoder hevc
[   0.286][v][vd] Selected decoder: hevc - HEVC (High Efficiency Video Coding)
[   0.318][i][vd] Using hardware decoding (d3d11va).
[   0.280][v][vo/gpu-next/libplacebo] Initialized libplacebo v7.371.0 (API v371)
'@
    $blOnlyEvidence = Get-NativeDvRuntimeEvidence -LogText $blOnlyLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    $blOnlyState = Get-NativeDvPipelineState -RuntimeEvidence $blOnlyEvidence -Invocation $fakeInvocation -SourceClassification 'FEL'
    Assert-Equal 'Inactive' $blOnlyState.Observed.FelComposition 'a running renderer without NLQ must report Inactive composition'
    Assert-Equal 'Absent' $blOnlyState.Observed.BlElPairing 'a running renderer without el_pair must report Absent pairing'
    Assert-Equal 1 $blOnlyState.Observed.DecoderInstances 'a single decoder instance must be reported honestly'
    Assert-True ($null -eq $blOnlyState.Observed.ElDecoder) 'no enhancement-layer decoder may be invented'
    Assert-True (@($blOnlyState.Observed.Degradation) -join ' ' -match 'base-layer-only') 'requested-but-uncomposed FEL must be named a base-layer-only result'
    Assert-True (@($blOnlyState.Observed.Degradation) -join ' ' -match 'Fewer than two') 'a missing second decoder must be named as degradation'

    $softwareLog = $blOnlyLog + "`n" + '[   0.4][v][vd] Using software decoding.'
    $softwareEvidence = Get-NativeDvRuntimeEvidence -LogText $softwareLog -RequestedEnhancementLayer $true -RequestedHardwareDecoder 'd3d11va'
    $softwareState = Get-NativeDvPipelineState -RuntimeEvidence $softwareEvidence -Invocation $fakeInvocation
    Assert-Equal 'Software' $softwareState.Observed.HardwareSurfaces 'software fallback must be visible in the observed model'
    Assert-True (@($softwareState.Observed.Degradation) -join ' ' -match 'Software decoding fallback') 'software fallback must be named as degradation'

    # --- Zero-media-scratch gate needs two independent measurements ------------
    $cleanDelta = [pscustomobject]@{ MediaPayloadBytes = 0L; MediaPayload = @(); Unknown = @(); BoundedStateBytes = 4771115L; UnknownBytes = 0L }
    $noCounters = Test-NativeDvZeroMediaScratch -FilesystemDelta $cleanDelta -ProcessIo $null
    Assert-Equal 'Unknown' $noCounters.Result 'a clean snapshot alone cannot prove zero scratch; transient writes are invisible to it'
    Assert-True (@($noCounters.Reasons) -join ' ' -match 'transient') 'the missing-counter limitation must be stated'

    $withCounters = Test-NativeDvZeroMediaScratch -FilesystemDelta $cleanDelta -ProcessIo ([pscustomobject]@{ WriteTransferBytes = 4731122L })
    Assert-Equal 'Zero' $withCounters.Result 'a clean snapshot plus a bounded process write total proves zero media scratch'
    Assert-Equal 4731122 $withCounters.ProcessWriteBytes 'the process write total must be reported'

    $bigWrite = Test-NativeDvZeroMediaScratch -FilesystemDelta $cleanDelta -ProcessIo ([pscustomobject]@{ WriteTransferBytes = 900000000L })
    Assert-Equal 'Unknown' $bigWrite.Result 'a large process write total must defeat the zero-scratch claim even when the snapshot is clean'

    $dirtyDelta = [pscustomobject]@{
        MediaPayloadBytes = 4096L
        MediaPayload = @([pscustomobject]@{ Path = 'C:\run\shadow.hevc' })
        Unknown = @(); BoundedStateBytes = 0L; UnknownBytes = 0L
    }
    $violated = Test-NativeDvZeroMediaScratch -FilesystemDelta $dirtyDelta -ProcessIo ([pscustomobject]@{ WriteTransferBytes = 4096L })
    Assert-Equal 'Violated' $violated.Result 'an observed media payload must fail the gate outright'

    if ($PSVersionTable.PSVersion.Major -ge 7) {
    $runnerPath = Join-Path $root 'scripts\Invoke-NativeDvExperiment.ps1'
    $orchestrationRoot = Join-Path $tempRoot 'orchestration'
    $plannedRun = & $runnerPath -SourcePath $sourcePath -RuntimeManifest $manifestPath `
        -OutputRoot $orchestrationRoot -RunId 'fake-orchestration' -EnhancementLayer yes `
        -StartSeconds 42 -DurationSeconds 10 -HardwareDecoder no -GpuApi auto -GpuContext auto -PlanOnly
    Assert-Equal ([IO.Path]::GetFullPath((Join-Path $orchestrationRoot 'fake-orchestration'))) $plannedRun 'plan-only runner must return its run directory'
    $plannedManifest = Get-Content -LiteralPath (Join-Path $plannedRun 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True $plannedManifest.PlanOnly 'plan-only state must be explicit'
    Assert-Equal ([IO.Path]::GetFullPath($sourcePath)) $plannedManifest.Source.Path 'runner must preserve the source path'
    Assert-Equal $runtimePath $plannedManifest.Invocation.Executable 'runner must preserve the pinned executable'
    Assert-True (@($plannedManifest.Invocation.Arguments | Where-Object { $_ -eq '--no-config' }).Count -eq 1) 'runner manifest must record exact argv'
    Assert-True ($plannedManifest.Invocation.Environment.TEMP -like "$plannedRun*") 'runner manifest must record isolated environment'
    Assert-True (Test-Path -LiteralPath (Join-Path $plannedRun 'filesystem-before.json')) 'runner must snapshot storage before work'
    Assert-True (Test-Path -LiteralPath (Join-Path $plannedRun 'source-identity-before.json')) 'runner must snapshot source identity before work'

    Add-Type -AssemblyName System.Drawing
    $offPng = Join-Path $tempRoot 'off.png'
    $onPng = Join-Path $tempRoot 'on.png'
    $bitmap = [Drawing.Bitmap]::new(2, 2)
    try {
        for ($y = 0; $y -lt 2; $y++) { for ($x = 0; $x -lt 2; $x++) { $bitmap.SetPixel($x, $y, [Drawing.Color]::FromArgb(255, 10, 20, 30)) } }
        $bitmap.Save($offPng, [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.SetPixel(1, 0, [Drawing.Color]::FromArgb(255, 20, 20, 30))
        $bitmap.Save($onPng, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    $comparisonPath = Join-Path $tempRoot 'comparison.json'
    $comparison = & (Join-Path $root 'scripts\Compare-NativeDvCaptures.ps1') -EnhancementOn $onPng -EnhancementOff $offPng -OutputPath $comparisonPath
    Assert-Equal 4 $comparison.PixelCount 'pixel comparison must report image size'
    Assert-Equal 1 $comparison.ChangedPixelCount 'pixel comparison must count changed pixels'
    Assert-Equal 10 $comparison.MaximumChannelDelta 'pixel comparison must report maximum channel delta'
    Assert-Equal 1 $comparison.BoundingBox.Left 'pixel comparison must report changed-pixel bounds'
    Assert-Equal 0 $comparison.BoundingBox.Top 'pixel comparison must report changed-pixel bounds'
    Assert-True (Test-Path -LiteralPath $comparisonPath) 'pixel comparison must persist machine-readable evidence'

    # The proof captures are 16-bit RGBA, but the fixture above is an 8-bit
    # System.Drawing bitmap, so the decode path that actually carries the
    # milestone's rendered claim was untested. Build 16-bit RGBA PNGs directly.
    # Stored (uncompressed) deflate blocks are used so this works on both
    # PowerShell 5.1 and 7 without depending on ZLibStream.
    function New-NativeDvTestPng16 {
        param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][uint16[]]$Pixels,
              [Parameter(Mandatory = $true)][int]$Width, [Parameter(Mandatory = $true)][int]$Height)

        # PowerShell parses an eight-hex-digit literal as a signed int, so
        # 0xFFFFFFFF is -1. Use long literals and mask explicitly throughout.
        $mask = [long]0xFFFFFFFFL
        $crcTable = New-Object long[] 256
        for ($n = 0; $n -lt 256; $n++) {
            $c = [long]$n
            for ($k = 0; $k -lt 8; $k++) {
                if ($c -band 1L) { $c = ((0xEDB88320L -bxor ($c -shr 1)) -band $mask) } else { $c = (($c -shr 1) -band $mask) }
            }
            $crcTable[$n] = $c
        }
        function Get-Crc32 {
            param([byte[]]$Bytes)
            $c = $mask
            foreach ($b in $Bytes) { $c = (($crcTable[[int](($c -bxor $b) -band 0xFF)] -bxor ($c -shr 8)) -band $mask) }
            return [uint32](($c -bxor $mask) -band $mask)
        }
        # The comma operator binds tighter than -band, so each element needs its own parentheses.
        function Get-BigEndian { param([uint32]$Value) return [byte[]]@((($Value -shr 24) -band 0xFF), (($Value -shr 16) -band 0xFF), (($Value -shr 8) -band 0xFF), ($Value -band 0xFF)) }
        function New-Chunk {
            param([string]$Type, [byte[]]$Data)
            $typeBytes = [Text.Encoding]::ASCII.GetBytes($Type)
            $payload = $typeBytes + $Data
            return (Get-BigEndian ([uint32]$Data.Length)) + $payload + (Get-BigEndian (Get-Crc32 $payload))
        }

        # Raw scanlines: filter byte 0, then big-endian 16-bit RGBA samples.
        $raw = [Collections.Generic.List[byte]]::new()
        $index = 0
        for ($y = 0; $y -lt $Height; $y++) {
            [void]$raw.Add(0)
            for ($x = 0; $x -lt ($Width * 4); $x++) {
                $sample = $Pixels[$index]; $index++
                [void]$raw.Add([byte](($sample -shr 8) -band 0xFF))
                [void]$raw.Add([byte]($sample -band 0xFF))
            }
        }
        $rawBytes = $raw.ToArray()

        # zlib container around a single stored deflate block.
        $adlerA = [uint32]1; $adlerB = [uint32]0
        foreach ($b in $rawBytes) { $adlerA = ($adlerA + $b) % 65521; $adlerB = ($adlerB + $adlerA) % 65521 }
        $len = $rawBytes.Length
        $zlib = [Collections.Generic.List[byte]]::new()
        [void]$zlib.Add(0x78); [void]$zlib.Add(0x01)
        [void]$zlib.Add(0x01)
        [void]$zlib.Add([byte]($len -band 0xFF)); [void]$zlib.Add([byte](($len -shr 8) -band 0xFF))
        [void]$zlib.Add([byte]((-bnot $len) -band 0xFF)); [void]$zlib.Add([byte](((-bnot $len) -shr 8) -band 0xFF))
        $zlib.AddRange($rawBytes)
        $zlib.AddRange([byte[]](Get-BigEndian ([uint32](($adlerB -shl 16) -bor $adlerA))))

        $ihdr = (Get-BigEndian ([uint32]$Width)) + (Get-BigEndian ([uint32]$Height)) + [byte[]]@(16, 6, 0, 0, 0)
        $png = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A) +
            (New-Chunk 'IHDR' $ihdr) + (New-Chunk 'IDAT' $zlib.ToArray()) + (New-Chunk 'IEND' @())
        [IO.File]::WriteAllBytes($Path, [byte[]]$png)
    }

    # 2x1 RGBA. Pixel 0 is identical in both; pixel 1 differs by 4096 in green
    # only. Alpha differs deliberately and must be ignored: the comparison scores
    # colour channels only, so an alpha-only change is not a rendered difference.
    $wide16Off = Join-Path $tempRoot 'off16.png'
    $wide16On = Join-Path $tempRoot 'on16.png'
    New-NativeDvTestPng16 -Path $wide16Off -Width 2 -Height 1 -Pixels ([uint16[]](1000, 2000, 3000, 65535,  1000, 2000, 3000, 65535))
    New-NativeDvTestPng16 -Path $wide16On  -Width 2 -Height 1 -Pixels ([uint16[]](1000, 2000, 3000, 12345,  1000, 6096, 3000, 65535))
    $compare16Path = Join-Path $tempRoot 'comparison16.json'
    $compare16 = & (Join-Path $root 'scripts\Compare-NativeDvCaptures.ps1') -EnhancementOn $wide16On -EnhancementOff $wide16Off -OutputPath $compare16Path
    Assert-Equal 16 $compare16.BitDepth '16-bit captures must decode at 16-bit depth'
    Assert-Equal 65535 $compare16.SampleMaximum '16-bit sample maximum must be reported'
    Assert-Equal 2 $compare16.PixelCount '16-bit geometry must be decoded correctly'
    Assert-Equal 1 $compare16.ChangedPixelCount 'an alpha-only change is not a rendered colour difference'
    Assert-Equal 4096 $compare16.MaximumChannelDelta '16-bit channel deltas must not be truncated to 8 bits'
    Assert-Equal 1 $compare16.BoundingBox.Left 'the changed 16-bit pixel must be located'

    # A capture that differs nowhere must score zero, which is what the
    # determinism control depends on being able to detect.
    $identical16 = Join-Path $tempRoot 'identical16.png'
    New-NativeDvTestPng16 -Path $identical16 -Width 2 -Height 1 -Pixels ([uint16[]](1000, 2000, 3000, 65535,  1000, 2000, 3000, 65535))
    $controlLike = & (Join-Path $root 'scripts\Compare-NativeDvCaptures.ps1') -EnhancementOn $identical16 -EnhancementOff $wide16Off -OutputPath (Join-Path $tempRoot 'comparison16-control.json')
    Assert-Equal 0 $controlLike.ChangedPixelCount 'identical captures must score zero changed pixels'
    Assert-Equal 0 $controlLike.MaximumChannelDelta 'identical captures must score zero maximum delta'
    }

    $monitored = Join-Path $tempRoot 'monitored'
    [void][IO.Directory]::CreateDirectory($monitored)
    $before = Get-NativeDvFileSnapshot -Path $monitored
    [void][IO.Directory]::CreateDirectory((Join-Path $monitored 'state\shader-cache'))
    [IO.File]::WriteAllText((Join-Path $monitored 'player.log'), 'bounded log')
    [IO.File]::WriteAllText((Join-Path $monitored 'state\manifest.json'), '{}')
    [IO.File]::WriteAllBytes((Join-Path $monitored 'state\shader-cache\cache.bin'), [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes((Join-Path $monitored 'shadow.hevc'), [byte[]](1, 2, 3, 4))
    [IO.File]::WriteAllBytes((Join-Path $monitored 'mystery.dat'), [byte[]](1, 2, 3))
    $after = Get-NativeDvFileSnapshot -Path $monitored
    $delta = Compare-NativeDvFileSnapshot -Before $before -After $after -BoundedStateMaxBytes 1024
    Assert-Equal 3 $delta.BoundedState.Count 'bounded state must be separated'
    Assert-Equal 1 $delta.MediaPayload.Count 'media payload must be detected regardless of size'
    Assert-Equal 1 $delta.Unknown.Count 'unknown writes must remain unknown'
    Assert-True (-not $delta.Success) 'media or unknown writes must fail the delta'
    Assert-Equal 'MediaPayload' (Test-NativeDvMediaArtifact -Path (Join-Path $monitored 'shadow.hevc') -Length 4 -GrowthBytes 4 -BoundedStateMaxBytes 1024).Classification 'HEVC is media payload'
    Assert-Equal 'MediaPayload' (Test-NativeDvMediaArtifact -Path (Join-Path $monitored 'progressive-cache.bin') -Length 2048 -GrowthBytes 2048 -BoundedStateMaxBytes 1024).Classification 'growing oversized cache is media payload'
    Assert-Equal 'Unknown' (Test-NativeDvMediaArtifact -Path (Join-Path $monitored 'oversized.log') -Length 2048 -GrowthBytes 2048 -BoundedStateMaxBytes 1024).Classification 'oversized ordinary state is not silently accepted'

    # --- Native playback must not be wired to Compatibility Export -----------
    # The evidence adapter extracts an elementary stream, which for the authored
    # source would be roughly 73 GB of scratch. The native playback lane must never
    # reach it, nor the conversion planner or executor. This is a source-level
    # boundary so that wiring them together later fails here rather than in
    # production.
    foreach ($laneFile in @('src\AdaptiveMedia.App\NativeDvLane.cs', 'src\AdaptiveMedia.App\NativeDvPlayback.cs', 'src\AdaptiveMedia.App\NativeDvRuntimeStore.cs')) {
        $lanePath = Join-Path $root $laneFile
        Assert-True (Test-Path -LiteralPath $lanePath) "native lane source must exist: $laneFile"
        $laneText = Get-Content -LiteralPath $lanePath -Raw
        # Strip comments entirely: this boundary is about code references, so prose
        # that explains why the lane avoids these types must not trip it.
        $laneCode = ($laneText -split "`n" | ForEach-Object { ($_ -replace '//.*$', '') }) -join "`n"
        foreach ($forbidden in @('DvEvidenceAdapter', 'DvMatroskaP81Executor', 'DvConversionPlanner', 'mkvextract', 'mkvmerge')) {
            Assert-True (-not ($laneCode -match [regex]::Escape($forbidden))) "$laneFile must not reference $forbidden"
        }
    }

    $identity = Get-NativeDvSourceIdentity -Path $sourcePath -SentinelBytes 32
    $same = Test-NativeDvSourceIdentity -Path $sourcePath -ExpectedIdentity $identity
    Assert-True $same.Matches 'unchanged source identity must pass'
    [IO.File]::WriteAllBytes($sourcePath, [byte[]](0..254))
    $changedLength = Test-NativeDvSourceIdentity -Path $sourcePath -ExpectedIdentity $identity
    Assert-True (-not $changedLength.Matches) 'changed source length must fail'
    Assert-True ($changedLength.Differences -contains 'Length') 'length difference must be named'
    [IO.File]::WriteAllBytes($sourcePath, [byte[]](0..255))
    [IO.File]::SetLastWriteTimeUtc($sourcePath, $identity.LastWriteTimeUtc.AddSeconds(5))
    $changedTime = Test-NativeDvSourceIdentity -Path $sourcePath -ExpectedIdentity $identity
    Assert-True (-not $changedTime.Matches) 'changed source timestamp must fail'
    Assert-True ($changedTime.Differences -contains 'LastWriteTimeUtc') 'timestamp difference must be named'
    [IO.File]::SetLastWriteTimeUtc($sourcePath, $identity.LastWriteTimeUtc)
    $bytes = [IO.File]::ReadAllBytes($sourcePath)
    $bytes[128] = $bytes[128] -bxor 0xff
    [IO.File]::WriteAllBytes($sourcePath, $bytes)
    [IO.File]::SetLastWriteTimeUtc($sourcePath, $identity.LastWriteTimeUtc)
    $changedSentinel = Test-NativeDvSourceIdentity -Path $sourcePath -ExpectedIdentity $identity
    Assert-True (-not $changedSentinel.Matches) 'changed sentinel content must fail'
    Assert-True ($changedSentinel.Differences -contains 'MiddleSha256') 'middle sentinel difference must be named'

    Write-Host "Native Dolby Vision experiment contract: PASS ($script:assertions assertions, $($PSVersionTable.PSVersion))"
} finally {
    $fullTemp = [IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($fullTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTemp).StartsWith('adaptive-media-native-dv-test-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $fullTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
