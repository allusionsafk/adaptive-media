#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NativeDvManifestData {
    param([Parameter(Mandatory = $true)][string]$RuntimeManifest)

    $manifestPath = (Resolve-Path -LiteralPath $RuntimeManifest).Path
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) { throw "Unsupported native DV runtime manifest schema: $($manifest.schemaVersion)" }
    if (-not $manifest.runtime.consoleLauncher.pathRelativeToManifest) { throw 'Runtime manifest does not name a console launcher.' }

    [pscustomobject]@{
        Path = $manifestPath
        Directory = Split-Path -Parent $manifestPath
        Value = $manifest
    }
}

function New-NativeDvInvocation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$RuntimeManifest,
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][bool]$EnhancementLayer,
        [Parameter(Mandatory = $true)][double]$StartSeconds,
        [Parameter(Mandatory = $true)][double]$DurationSeconds,
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$PipeName,
        [ValidateSet('auto-safe', 'nvdec', 'd3d11va', 'no')][string]$HardwareDecoder = 'auto-safe',
        [ValidateSet('auto', 'd3d11', 'vulkan')][string]$GpuApi = 'auto',
        [ValidateSet('auto', 'd3d11', 'winvk')][string]$GpuContext = 'auto'
    )

    $manifestData = Get-NativeDvManifestData -RuntimeManifest $RuntimeManifest
    $source = (Resolve-Path -LiteralPath $SourcePath).Path
    $run = [IO.Path]::GetFullPath($RunDirectory)
    $runtimeRelative = [string]$manifestData.Value.runtime.consoleLauncher.pathRelativeToManifest
    $executable = [IO.Path]::GetFullPath((Join-Path $manifestData.Directory $runtimeRelative))
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Pinned runtime is missing: $executable" }
    if ($executable.StartsWith('C:\mpv\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The experimental invocation cannot use the stable C:\mpv runtime.'
    }

    $expectedBytes = [long]$manifestData.Value.runtime.consoleLauncher.bytes
    $expectedHash = [string]$manifestData.Value.runtime.consoleLauncher.sha256
    $runtimeFile = Get-Item -LiteralPath $executable
    if ($runtimeFile.Length -ne $expectedBytes) { throw "Pinned runtime length mismatch: $executable" }
    $actualHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash.ToLowerInvariant()) { throw "Pinned runtime SHA-256 mismatch: $executable" }

    $state = Join-Path $run 'state'
    $temp = Join-Path $run 'temp'
    $log = Join-Path $run 'logs\mpv.log'
    $pipePath = "\\.\pipe\$PipeName"
    $layer = if ($EnhancementLayer) { 'yes' } else { 'no' }
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @(
        '--no-config',
        "--config-dir=$state",
        "--demuxer-cache-dir=$state",
        "--gpu-shader-cache-dir=$state",
        '--gpu-shader-cache=no',
        "--watch-later-dir=$state",
        '--save-position-on-quit=no',
        '--cache=no',
        '--cache-on-disk=no',
        "--input-ipc-server=$pipePath",
        "--log-file=$log",
        '--vo=gpu-next',
        "--hwdec=$HardwareDecoder",
        "--gpu-api=$GpuApi",
        "--gpu-context=$GpuContext",
        '--audio=no',
        '--sub=no',
        '--osd-level=0',
        '--input-default-bindings=no',
        '--input-vo-keyboard=no',
        '--terminal=no',
        '--geometry=1280x720',
        '--target-prim=bt.709',
        '--target-trc=bt.1886',
        '--tone-mapping=bt.2446a',
        '--screenshot-format=png',
        '--screenshot-high-bit-depth=yes',
        '--screenshot-tag-colorspace=yes',
        '--msg-level=all=warn,mkv=v,vd=v,vf=v,vo/gpu-next=trace',
        '--force-window=yes',
        '--pause=yes',
        "--start=$($StartSeconds.ToString('0.###', $culture))",
        "--vf=format=enhancement-layer=$layer",
        '--',
        $source
    )) {
        [void]$arguments.Add($argument)
    }

    [pscustomobject]@{
        Executable = $executable
        Arguments = [string[]]$arguments.ToArray()
        Environment = [pscustomobject][ordered]@{
            TEMP = $temp
            TMP = $temp
        }
        ManifestPath = $manifestData.Path
        SourcePath = $source
        RunDirectory = $run
        StateDirectory = $state
        TempDirectory = $temp
        LogPath = $log
        PipeName = $PipeName
        PipePath = $pipePath
        EnhancementLayer = $EnhancementLayer
        DurationSeconds = $DurationSeconds
        HardwareDecoder = $HardwareDecoder
        GpuApi = $GpuApi
        GpuContext = $GpuContext
    }
}

function Get-NativeDvRuntimeEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$LogText,
        [Parameter(Mandatory = $true)][bool]$RequestedEnhancementLayer,
        [Parameter(Mandatory = $true)][string]$RequestedHardwareDecoder
    )

    # The renderer trace is the only place this pinned runtime exposes composition
    # state, so every pattern here is tied to the manifest-pinned mpv/libplacebo
    # build. Nothing below may infer an observation from what was requested.
    $decoderOpenCount = [regex]::Matches($LogText, '(?im)\[vd\][^
]*Opening decoder hevc\s*$').Count
    $selectedDecoders = @([regex]::Matches($LogText, '(?im)\[vd\]\s*Selected decoder:\s*([^\r\n]+)') | ForEach-Object { $_.Groups[1].Value.Trim() })
    $apiMatch = [regex]::Match($LogText, '(?im)Initialized libplacebo .*\(API v(?<api>\d+)\)')
    $hwdecMatch = [regex]::Match($LogText, '(?im)\[vd\]\s*Using hardware decoding \((?<name>[^)]+)\)')
    $elPairMatch = [regex]::Match($LogText, '(?im)\[vf\]\s*\[el_pair\]\s*(?<format>[^\r\n]+)')
    $softwareFallback = $LogText -match '(?im)\[vd\].*Using software decoding\.'
    $nlqShader = $LogText -match '(?im)sh_dovi_compose_nlq'
    $enhancementGpu = $LogText -match '(?im)Spent [0-9.]+ ms on shader:[^\r\n]*enhancement layer'
    # A renderer that never initialised cannot support a negative composition
    # claim; without this the absent-evidence case would read as "not composing".
    $rendererObserved = $apiMatch.Success

    # '[vo/gpu-next] Loading failed.' is emitted when gpu-next declines an optional
    # hwdec interop driver (for example 'd3d11-egl') after another driver already
    # succeeded. It is not a device-creation failure and must never be reported as
    # one. Count it separately so the benign signal is preserved, not discarded.
    $interopProbeFailures = [regex]::Matches($LogText, "(?im)\[vo/gpu-next\]\s*Loading failed\.\s*$").Count
    $deviceFailure = $LogText -match '(?im)(Could not create device|Failed to create (the )?(d3d11 |vulkan )?device|Failed to initialize [^\r\n]*(device|context))'

    [pscustomobject]@{
        RequestedEnhancementLayer = $RequestedEnhancementLayer
        RequestedHardwareDecoder = $RequestedHardwareDecoder
        RendererObserved = [bool]$rendererObserved
        Profile7Splitter = [bool]($LogText -match 'Dolby Vision Profile 7 splitter: BL stream \d+, virtual EL stream \d+ \(dependent_track\)')
        HevcDecoderOpenCount = $decoderOpenCount
        SelectedDecoders = [string[]]$selectedDecoders
        ElPair = [bool]$elPairMatch.Success
        ElPairFormat = if ($elPairMatch.Success) { $elPairMatch.Groups['format'].Value.Trim() } else { $null }
        LibplaceboApi = if ($apiMatch.Success) { [int]$apiMatch.Groups['api'].Value } else { $null }
        NlqCompositionShader = [bool]$nlqShader
        EnhancementLayerGpuStage = [bool]$enhancementGpu
        # Purely observed. The request is reported alongside it, never folded into it.
        CompositionObserved = [bool]($nlqShader -and $enhancementGpu)
        HardwareDecodingObserved = [bool]$hwdecMatch.Success
        HardwareDecoderInUse = if ($hwdecMatch.Success) { $hwdecMatch.Groups['name'].Value.Trim() } else { $null }
        SoftwareFallbackObserved = [bool]$softwareFallback
        HwdecInteropProbeFailures = $interopProbeFailures
        RendererDeviceFailureObserved = [bool]$deviceFailure
    }
}

function Get-NativeDvMelFelClassification {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$DoviToolSummary = '',
        [Parameter(Mandatory = $true)][object]$RuntimeEvidence
    )

    $evidence = [Collections.Generic.List[object]]::new()
    $toolState = 'Unknown'
    if ($DoviToolSummary -match '(?im)Profile:\s*7\s*\(FEL\)') {
        $toolState = 'FEL'
        $evidence.Add([pscustomobject]@{ Source = 'dovi_tool'; Fact = 'Profile 7 (FEL)' })
    } elseif ($DoviToolSummary -match '(?im)Profile:\s*7\s*\(MEL\)') {
        $toolState = 'MEL'
        $evidence.Add([pscustomobject]@{ Source = 'dovi_tool'; Fact = 'Profile 7 (MEL)' })
    }
    $runtimeFel = [bool]($RuntimeEvidence.NlqCompositionShader -and $RuntimeEvidence.EnhancementLayerGpuStage)
    if ($runtimeFel) {
        $evidence.Add([pscustomobject]@{ Source = 'mpv/libplacebo'; Fact = 'NLQ composition shader and enhancement-layer GPU stage observed' })
    }

    $state = if ($toolState -eq 'MEL' -and $runtimeFel) {
        'Unknown'
    } elseif ($toolState -ne 'Unknown') {
        $toolState
    } elseif ($runtimeFel) {
        'FEL'
    } else {
        'Unknown'
    }
    [pscustomobject]@{
        MelFel = $state
        Contradiction = [bool]($toolState -eq 'MEL' -and $runtimeFel)
        Evidence = [object[]]$evidence.ToArray()
    }
}

function Get-NativeDvFileSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$Path,
        [long]$HashMaxBytes = 16777216
    )

    $entries = [Collections.Generic.List[object]]::new()
    foreach ($requestedPath in $Path) {
        $absolute = [IO.Path]::GetFullPath($requestedPath)
        if (-not (Test-Path -LiteralPath $absolute)) { continue }
        $item = Get-Item -LiteralPath $absolute
        $files = if ($item.PSIsContainer) {
            @(Get-ChildItem -LiteralPath $absolute -Recurse -File -Force)
        } else {
            @($item)
        }
        foreach ($file in $files) {
            $hash = $null
            if ($file.Length -le $HashMaxBytes) {
                $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            }
            $entries.Add([pscustomobject]@{
                Path = [IO.Path]::GetFullPath($file.FullName)
                Length = [long]$file.Length
                LastWriteTimeUtc = $file.LastWriteTimeUtc
                Sha256 = $hash
            })
        }
    }
    return [object[]]$entries.ToArray()
}

function Test-NativeDvMediaArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$Length,
        [long]$GrowthBytes = 0,
        [long]$BoundedStateMaxBytes = 8388608
    )

    $absolute = [IO.Path]::GetFullPath($Path)
    $extension = [IO.Path]::GetExtension($absolute).ToLowerInvariant()
    $name = [IO.Path]::GetFileName($absolute).ToLowerInvariant()
    $normalized = $absolute.Replace('/', '\').ToLowerInvariant()
    $mediaExtensions = @('.hevc', '.h265', '.265', '.mkv', '.mp4', '.m4v', '.mov', '.ts', '.m2ts', '.webm', '.avi', '.mxf', '.vob', '.eac3', '.ac3', '.aac', '.wav', '.flac')
    $boundedExtensions = @('.log', '.json', '.jsonl', '.csv', '.txt', '.png')

    if ($mediaExtensions -contains $extension) {
        return [pscustomobject]@{ Classification = 'MediaPayload'; Reason = "media extension $extension"; Path = $absolute; Length = $Length; GrowthBytes = $GrowthBytes }
    }
    if (($name -match '(progress|stream|media|demux).*(cache|buffer)|(cache|buffer).*(progress|stream|media|demux)') -and
        ($Length -gt $BoundedStateMaxBytes -or $GrowthBytes -gt $BoundedStateMaxBytes)) {
        return [pscustomobject]@{ Classification = 'MediaPayload'; Reason = 'progressively growing media/cache name exceeds bounded-state ceiling'; Path = $absolute; Length = $Length; GrowthBytes = $GrowthBytes }
    }
    $knownStatePath = $normalized -match '\\(state|logs|captures|shader-cache|watch-later)(\\|$)'
    if ($Length -le $BoundedStateMaxBytes -and (($boundedExtensions -contains $extension) -or $knownStatePath)) {
        return [pscustomobject]@{ Classification = 'BoundedState'; Reason = 'known bounded runtime/evidence state'; Path = $absolute; Length = $Length; GrowthBytes = $GrowthBytes }
    }
    return [pscustomobject]@{ Classification = 'Unknown'; Reason = 'write is neither known bounded state nor a recognized media payload'; Path = $absolute; Length = $Length; GrowthBytes = $GrowthBytes }
}

function Compare-NativeDvFileSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowNull()][AllowEmptyCollection()][object[]]$Before,
        [Parameter(Mandatory = $true)][AllowNull()][AllowEmptyCollection()][object[]]$After,
        [long]$BoundedStateMaxBytes = 8388608
    )

    $beforeByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $afterByPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $beforeEntries = @($Before | Where-Object { $null -ne $_ })
    $afterEntries = @($After | Where-Object { $null -ne $_ })
    foreach ($entry in $beforeEntries) { $beforeByPath[[string]$entry.Path] = $entry }
    foreach ($entry in $afterEntries) { $afterByPath[[string]$entry.Path] = $entry }

    $changes = [Collections.Generic.List[object]]::new()
    foreach ($entry in $afterEntries) {
        $previous = $null
        $created = -not $beforeByPath.TryGetValue([string]$entry.Path, [ref]$previous)
        if (-not $created -and $previous.Length -eq $entry.Length -and $previous.LastWriteTimeUtc -eq $entry.LastWriteTimeUtc -and $previous.Sha256 -eq $entry.Sha256) { continue }
        $growth = if ($created) { [long]$entry.Length } else { [long]$entry.Length - [long]$previous.Length }
        $classification = Test-NativeDvMediaArtifact -Path $entry.Path -Length $entry.Length -GrowthBytes $growth -BoundedStateMaxBytes $BoundedStateMaxBytes
        $changes.Add([pscustomobject]@{
            Path = $entry.Path
            Change = if ($created) { 'Created' } else { 'Modified' }
            BeforeLength = if ($created) { 0L } else { [long]$previous.Length }
            AfterLength = [long]$entry.Length
            GrowthBytes = $growth
            Classification = $classification.Classification
            Reason = $classification.Reason
        })
    }
    foreach ($entry in $beforeEntries) {
        if (-not $afterByPath.ContainsKey([string]$entry.Path)) {
            $changes.Add([pscustomobject]@{
                Path = $entry.Path
                Change = 'Deleted'
                BeforeLength = [long]$entry.Length
                AfterLength = 0L
                GrowthBytes = -[long]$entry.Length
                Classification = 'Unknown'
                Reason = 'file was deleted during the measured interval'
            })
        }
    }

    $bounded = @($changes | Where-Object Classification -eq 'BoundedState')
    $media = @($changes | Where-Object Classification -eq 'MediaPayload')
    $unknown = @($changes | Where-Object Classification -eq 'Unknown')
    $boundedBytes = if ($bounded.Count) { [long](($bounded | Measure-Object -Property AfterLength -Sum).Sum) } else { 0L }
    $mediaBytes = if ($media.Count) { [long](($media | Measure-Object -Property AfterLength -Sum).Sum) } else { 0L }
    $unknownBytes = if ($unknown.Count) { [long](($unknown | Measure-Object -Property AfterLength -Sum).Sum) } else { 0L }
    [pscustomobject]@{
        Success = ($media.Count -eq 0 -and $unknown.Count -eq 0)
        Changes = [object[]]$changes.ToArray()
        BoundedState = $bounded
        MediaPayload = $media
        Unknown = $unknown
        BoundedStateBytes = $boundedBytes
        MediaPayloadBytes = $mediaBytes
        UnknownBytes = $unknownBytes
    }
}

function Get-NativeDvSentinelHash {
    param([string]$Path, [long]$Offset, [int]$Count)

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [void]$stream.Seek($Offset, [IO.SeekOrigin]::Begin)
        $buffer = New-Object byte[] $Count
        $total = 0
        while ($total -lt $Count) {
            $read = $stream.Read($buffer, $total, $Count - $total)
            if ($read -eq 0) { break }
            $total += $read
        }
        if ($total -ne $Count) { throw "Unable to read source sentinel at offset $Offset." }
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash($buffer))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose() }
    } finally {
        $stream.Dispose()
    }
}

function Get-NativeDvSourceIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(1, 16777216)][int]$SentinelBytes = 16777216
    )

    $absolute = (Resolve-Path -LiteralPath $Path).Path
    $file = Get-Item -LiteralPath $absolute
    if ($file.PSIsContainer) { throw "Source is not a file: $absolute" }
    $sample = [int][Math]::Min([long]$SentinelBytes, [long]$file.Length)
    $middleOffset = [long][Math]::Floor(([double]$file.Length - $sample) / 2)
    $lastOffset = [long]$file.Length - $sample
    [pscustomobject]@{
        Path = $absolute
        Length = [long]$file.Length
        LastWriteTimeUtc = $file.LastWriteTimeUtc
        SentinelBytes = $sample
        FirstOffset = 0L
        FirstSha256 = Get-NativeDvSentinelHash -Path $absolute -Offset 0 -Count $sample
        MiddleOffset = $middleOffset
        MiddleSha256 = Get-NativeDvSentinelHash -Path $absolute -Offset $middleOffset -Count $sample
        LastOffset = $lastOffset
        LastSha256 = Get-NativeDvSentinelHash -Path $absolute -Offset $lastOffset -Count $sample
    }
}

function Test-NativeDvSourceIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$ExpectedIdentity
    )

    $actual = Get-NativeDvSourceIdentity -Path $Path -SentinelBytes ([int]$ExpectedIdentity.SentinelBytes)
    $differences = [Collections.Generic.List[string]]::new()
    foreach ($property in @('Path', 'Length', 'LastWriteTimeUtc', 'FirstSha256', 'MiddleSha256', 'LastSha256')) {
        if ($actual.$property -ne $ExpectedIdentity.$property) { $differences.Add($property) }
    }
    [pscustomobject]@{
        Matches = ($differences.Count -eq 0)
        Differences = [string[]]$differences.ToArray()
        Expected = $ExpectedIdentity
        Actual = $actual
    }
}

function Get-NativeDvPipelineState {
    <#
    .SYNOPSIS
    Reduce a run's evidence into an explicit Requested/Observed pipeline record.

    .DESCRIPTION
    Requested values describe what the harness asked the runtime to do. Observed
    values describe only what the runtime demonstrably did. A value that cannot be
    established from evidence stays 'Unknown'; it is never promoted to 'Inactive'
    or to a false boolean, because "we did not see it" and "it did not happen" are
    different claims.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$RuntimeEvidence,
        [Parameter(Mandatory = $true)][object]$Invocation,
        [AllowNull()][object]$IpcSnapshot = $null,
        [AllowNull()][object]$FilesystemDelta = $null,
        [AllowNull()][object]$ProcessIo = $null,
        [AllowEmptyString()][string]$SourceClassification = 'Unknown'
    )

    $rendererRan = [bool]$RuntimeEvidence.RendererObserved
    $decoders = @($RuntimeEvidence.SelectedDecoders)
    $degradation = [Collections.Generic.List[string]]::new()

    # Composition is the FEL claim itself, so it gets the strictest treatment.
    $composition = if (-not $rendererRan) {
        'Unknown'
    } elseif ($RuntimeEvidence.CompositionObserved) {
        'Active'
    } else {
        'Inactive'
    }

    $pairing = if ($RuntimeEvidence.ElPair) {
        'Active'
    } elseif (-not $rendererRan) {
        'Unknown'
    } else {
        'Absent'
    }

    # Two HEVC decoder instances is the BL+EL signature for this pinned build.
    $blDecoder = if ($decoders.Count -ge 1) { $decoders[0] } else { $null }
    $elDecoder = if ($decoders.Count -ge 2) { $decoders[1] } else { $null }
    if ($rendererRan -and $decoders.Count -lt 2) {
        $degradation.Add('Fewer than two HEVC decoder instances were observed; the enhancement layer was not separately decoded.')
    }

    $hardwareSurfaces = if ($RuntimeEvidence.SoftwareFallbackObserved) {
        'Software'
    } elseif ($RuntimeEvidence.HardwareDecodingObserved) {
        'Active'
    } else {
        'Unknown'
    }
    if ($RuntimeEvidence.SoftwareFallbackObserved) {
        $degradation.Add('Software decoding fallback was observed.')
    }
    if ($RuntimeEvidence.RendererDeviceFailureObserved) {
        $degradation.Add('A renderer device or context creation failure was observed.')
    }
    if ($RuntimeEvidence.RequestedEnhancementLayer -and $composition -eq 'Inactive') {
        $degradation.Add('Full enhancement-layer composition was requested but the renderer did not compose it; this is a base-layer-only result.')
    }

    # RPU state comes from container/splitter evidence, not from composition: a run
    # that suppresses composition still carries RPU metadata.
    $rpu = if ($RuntimeEvidence.Profile7Splitter) { 'Present' } else { 'Unknown' }

    $mediaScratchBytes = $null
    $boundedStateBytes = $null
    $unknownBytes = $null
    if ($null -ne $FilesystemDelta) {
        $mediaScratchBytes = [long]$FilesystemDelta.MediaPayloadBytes
        $boundedStateBytes = [long]$FilesystemDelta.BoundedStateBytes
        $unknownBytes = [long]$FilesystemDelta.UnknownBytes
    }
    $processWriteBytes = $null
    if ($null -ne $ProcessIo) { $processWriteBytes = [long]$ProcessIo.WriteTransferBytes }

    $graphicsApi = $null
    $graphicsContext = $null
    $hwdecCurrent = $null
    if ($null -ne $IpcSnapshot) {
        $names = @($IpcSnapshot.PSObject.Properties.Name)
        if ($names -contains 'gpu-api') { $graphicsApi = $IpcSnapshot.'gpu-api' }
        if ($names -contains 'gpu-context') { $graphicsContext = $IpcSnapshot.'gpu-context' }
        if ($names -contains 'hwdec-current') { $hwdecCurrent = $IpcSnapshot.'hwdec-current' }
    }

    [pscustomobject]@{
        SchemaVersion = 1
        Requested = [pscustomobject][ordered]@{
            SourcePath = $Invocation.SourcePath
            SourceClassification = $SourceClassification
            NativeProfile7Playback = $true
            EnhancementLayer = [bool]$Invocation.EnhancementLayer
            RuntimeExecutable = $Invocation.Executable
            Renderer = 'gpu-next'
            GpuApi = $Invocation.GpuApi
            GpuContext = $Invocation.GpuContext
            HardwareDecoder = $Invocation.HardwareDecoder
        }
        Observed = [pscustomobject][ordered]@{
            RendererObserved = $rendererRan
            Renderer = if ($rendererRan) { 'gpu-next' } else { $null }
            GraphicsApi = $graphicsApi
            GraphicsContext = $graphicsContext
            LibplaceboApi = $RuntimeEvidence.LibplaceboApi
            Profile7Splitter = [bool]$RuntimeEvidence.Profile7Splitter
            DecoderInstances = [int]$RuntimeEvidence.HevcDecoderOpenCount
            BlDecoder = $blDecoder
            ElDecoder = $elDecoder
            BlElPairing = $pairing
            BlElPairFormat = $RuntimeEvidence.ElPairFormat
            RpuState = $rpu
            FelComposition = $composition
            HardwareSurfaces = $hardwareSurfaces
            HardwareDecoderInUse = $RuntimeEvidence.HardwareDecoderInUse
            HwdecCurrent = $hwdecCurrent
            HwdecInteropProbeFailures = [int]$RuntimeEvidence.HwdecInteropProbeFailures
            MediaScratchBytes = $mediaScratchBytes
            BoundedStateBytes = $boundedStateBytes
            UnknownWriteBytes = $unknownBytes
            ProcessWriteBytes = $processWriteBytes
            Degradation = [string[]]$degradation.ToArray()
        }
    }
}

function Test-NativeDvZeroMediaScratch {
    <#
    .SYNOPSIS
    Decide the zero-media-scratch claim from two independent measurements.

    .DESCRIPTION
    A directory snapshot taken before and after a run cannot see a media-sized file
    that is created and deleted while the run is in flight. The process write-byte
    counter can: it accumulates every byte the process wrote anywhere, including to
    files that no longer exist. Both must agree before the claim is made, and a
    missing process counter downgrades the result to Unknown rather than passing it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object]$FilesystemDelta,
        [AllowNull()][object]$ProcessIo = $null,
        [long]$ProcessWriteCeilingBytes = 33554432
    )

    $reasons = [Collections.Generic.List[string]]::new()
    $mediaBytes = [long]$FilesystemDelta.MediaPayloadBytes
    $mediaCount = @($FilesystemDelta.MediaPayload).Count
    $unknownCount = @($FilesystemDelta.Unknown).Count
    if ($mediaCount -gt 0) { $reasons.Add("$mediaCount media-payload file(s) totalling $mediaBytes bytes were created.") }
    if ($unknownCount -gt 0) { $reasons.Add("$unknownCount unclassified write(s) were observed.") }

    $processWriteBytes = $null
    if ($null -ne $ProcessIo) { $processWriteBytes = [long]$ProcessIo.WriteTransferBytes }
    if ($null -eq $processWriteBytes) {
        $reasons.Add('Process write-byte counters were unavailable, so transient media-sized writes could not be excluded.')
    } elseif ($processWriteBytes -gt $ProcessWriteCeilingBytes) {
        $reasons.Add("The process wrote $processWriteBytes bytes, above the $ProcessWriteCeilingBytes-byte bounded-state ceiling.")
    }

    $snapshotClean = ($mediaCount -eq 0 -and $unknownCount -eq 0)
    $processClean = ($null -ne $processWriteBytes -and $processWriteBytes -le $ProcessWriteCeilingBytes)
    $result = if ($snapshotClean -and $processClean) {
        'Zero'
    } elseif (-not $snapshotClean) {
        'Violated'
    } else {
        'Unknown'
    }

    [pscustomobject]@{
        Result = $result
        SnapshotClean = $snapshotClean
        ProcessCounterClean = $processClean
        MediaPayloadBytes = $mediaBytes
        MediaPayloadCount = $mediaCount
        UnknownCount = $unknownCount
        ProcessWriteBytes = $processWriteBytes
        ProcessWriteCeilingBytes = $ProcessWriteCeilingBytes
        Reasons = [string[]]$reasons.ToArray()
    }
}

Export-ModuleMember -Function @(
    'New-NativeDvInvocation',
    'Get-NativeDvRuntimeEvidence',
    'Get-NativeDvPipelineState',
    'Test-NativeDvZeroMediaScratch',
    'Get-NativeDvMelFelClassification',
    'Get-NativeDvFileSnapshot',
    'Compare-NativeDvFileSnapshot',
    'Test-NativeDvMediaArtifact',
    'Get-NativeDvSourceIdentity',
    'Test-NativeDvSourceIdentity'
)
