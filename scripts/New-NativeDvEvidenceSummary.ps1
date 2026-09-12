#requires -Version 7.0
<#
.SYNOPSIS
Derive the native Dolby Vision direct-playback evidence summary from run artifacts.

.DESCRIPTION
The integration gate is computed here rather than transcribed by hand, so the
committed summary cannot drift from the evidence it claims to describe. Rerun
this after any experiment pass and commit the regenerated file.

The gate is 'supported' only when BOTH categories hold:

  execution evidence - the runtime demonstrably split Profile 7, opened a second
  HEVC decoder, paired BL and EL, and composed NLQ in libplacebo; and

  rendered evidence  - a same-configuration control pass is pixel-identical
  (so the renderer is deterministic at the compared timestamp) AND the
  enhancement-layer A/B pass differs at the same authored timestamp.

Without the control, an A/B difference could be renderer nondeterminism rather
than enhancement-layer contribution, so a missing or non-identical control
downgrades the gate instead of passing it.
#>
[CmdletBinding()]
param(
    [string]$RunRoot = (Join-Path $PSScriptRoot '..\.artifacts\native-dv\runs'),
    [string]$ComparisonRoot = (Join-Path $PSScriptRoot '..\.artifacts\native-dv\comparisons'),
    [string]$RuntimeManifest = (Join-Path $PSScriptRoot '..\docs\native-dv-p7\runtime-manifest.json'),
    [string]$FelOnRunId = 'proof2-fel-on-a',
    [string]$FelOnControlRunId = 'proof2-fel-on-b',
    [string]$FelOffRunId = 'proof2-fel-off',
    [string]$ControlComparison = 'proof2-control-on-vs-on.json',
    [string]$AbComparison = 'proof2-on-vs-off.json',
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\docs\native-dv-p7\direct-playback-summary.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Json {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Required evidence file is missing: $Path" }
    Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}

function Get-RunEvidence {
    param([Parameter(Mandatory = $true)][string]$RunId)
    $dir = [IO.Path]::GetFullPath((Join-Path $RunRoot $RunId))
    $metrics = @(Import-Csv -LiteralPath (Join-Path $dir 'metrics.csv'))
    $sustained = @($metrics | Where-Object Phase -eq 'sustained')
    $manifest = Read-Json (Join-Path $dir 'manifest.json')
    $snapshots = Read-Json (Join-Path $dir 'ipc-snapshots.json')
    $seeks = @(Read-Json (Join-Path $dir 'seeks.json'))

    $startupMs = $null
    foreach ($line in (Get-Content -LiteralPath (Join-Path $dir 'events.jsonl'))) {
        if (-not $line) { continue }
        $event = $line | ConvertFrom-Json
        if ($event.Event -eq 'ready') { $startupMs = [int]$event.Data.Milliseconds }
    }

    function Range {
        param([string]$Column)
        $values = @($metrics | ForEach-Object { $_.$Column } | Where-Object { $_ -ne '' -and $null -ne $_ } | ForEach-Object { [double]$_ })
        if ($values.Count -eq 0) { return $null }
        [ordered]@{
            Minimum = [long]($values | Measure-Object -Minimum).Minimum
            Maximum = [long]($values | Measure-Object -Maximum).Maximum
            First = [long]$values[0]
            Last = [long]$values[-1]
            GrowthFirstToLast = [long]($values[-1] - $values[0])
        }
    }

    function SustainedMean {
        param([string]$Column)
        $values = @($sustained | ForEach-Object { $_.$Column } | Where-Object { $_ -ne '' -and $null -ne $_ } | ForEach-Object { [double]$_ })
        if ($values.Count -eq 0) { return $null }
        [Math]::Round((($values | Measure-Object -Average).Average), 3)
    }

    # CPU is recorded as cumulative process CPU time; a percentage is only
    # meaningful against the wall-clock window it was accumulated over.
    $cpuPercentOfMachine = $null
    if ($sustained.Count -ge 2) {
        $first = $sustained[0]
        $last = $sustained[-1]
        $wall = ([datetime]$last.TimestampUtc - [datetime]$first.TimestampUtc).TotalSeconds
        $cpu = [double]$last.CpuTotalSeconds - [double]$first.CpuTotalSeconds
        $logical = [int]$manifest.Host.LogicalProcessors
        if ($wall -gt 0 -and $logical -gt 0) {
            $cpuPercentOfMachine = [Math]::Round(($cpu / $wall / $logical * 100), 4)
        }
    }

    [ordered]@{
        RunId = $RunId
        RequestedEnhancementLayer = [bool]$manifest.Invocation.EnhancementLayer
        PipelineState = Read-Json (Join-Path $dir 'pipeline-state.json')
        ZeroMediaScratch = Read-Json (Join-Path $dir 'zero-media-scratch.json')
        Capture = Read-Json (Join-Path $dir 'capture.json')
        SourceIdentityMatchesAfterRun = [bool](Read-Json (Join-Path $dir 'source-identity-after.json')).Matches
        StartupToFirstVideoParamsMs = $startupMs
        Seeks = [ordered]@{
            Count = $seeks.Count
            AllWithinTolerance = [bool](@($seeks | Where-Object { -not $_.WithinTolerance }).Count -eq 0)
            RecoveryMillisecondsMinimum = [int](($seeks | Measure-Object RecoveryMilliseconds -Minimum).Minimum)
            RecoveryMillisecondsMaximum = [int](($seeks | Measure-Object RecoveryMilliseconds -Maximum).Maximum)
        }
        Frames = [ordered]@{
            DroppedFrames = [int]$snapshots.Seeks.'frame-drop-count'
            DecoderDroppedFrames = [int]$snapshots.Seeks.'decoder-frame-drop-count'
            VoDelayedFrames = [int]$snapshots.Seeks.'vo-delayed-frame-count'
            ContainerFps = [double]$snapshots.Launch.'container-fps'
            DisplayFps = [double]$snapshots.Launch.'display-fps'
        }
        Memory = [ordered]@{
            WorkingSetBytes = Range 'WorkingSetBytes'
            PrivateBytes = Range 'PrivateBytes'
            ProcessGpuDedicatedBytes = Range 'ProcessGpuDedicatedBytes'
            ProcessGpuSharedBytes = Range 'ProcessGpuSharedBytes'
        }
        Io = [ordered]@{
            ReadTransferBytes = Range 'IoReadTransferBytes'
            WriteTransferBytes = Range 'IoWriteTransferBytes'
        }
        Utilisation = [ordered]@{
            CpuPercentOfAllLogicalProcessors = $cpuPercentOfMachine
            LogicalProcessors = [int]$manifest.Host.LogicalProcessors
            SustainedVideoDecodePercentMean = SustainedMean 'ProcessGpuVideoDecodePercent'
            SustainedThreeDPercentMean = SustainedMean 'ProcessGpuThreeDPercent'
        }
    }
}

$runtime = Read-Json $RuntimeManifest
# The manifest names whatever runtime is pinned today, but the evidence was
# gathered by whichever runtime actually ran. Stamping a repinned manifest onto
# older runs would silently attribute the proof to a build that never produced
# it, so refuse when they disagree.
$evidenceVersion = [string](Read-Json (Join-Path ([IO.Path]::GetFullPath((Join-Path $RunRoot $FelOnRunId))) 'ipc-snapshots.json')).Launch.'mpv-version'
$manifestCommit = [string]$runtime.mpv.commit
$shortCommit = if ($manifestCommit.Length -ge 9) { $manifestCommit.Substring(0, 9) } else { $manifestCommit }
if ($evidenceVersion -and -not $evidenceVersion.Contains($shortCommit)) {
    throw ("The pinned manifest describes mpv $manifestCommit but the evidence in '$FelOnRunId' was produced by '$evidenceVersion'. " +
        'Pass -RuntimeManifest pointing at the manifest those runs used, or regenerate the evidence with the current runtime.')
}
$felOn = Get-RunEvidence -RunId $FelOnRunId
$felOnControl = Get-RunEvidence -RunId $FelOnControlRunId
$felOff = Get-RunEvidence -RunId $FelOffRunId
$control = Read-Json (Join-Path $ComparisonRoot $ControlComparison)
$ab = Read-Json (Join-Path $ComparisonRoot $AbComparison)

$observedOn = $felOn.PipelineState.Observed
$observedOff = $felOff.PipelineState.Observed

$executionChecks = [ordered]@{
    Profile7SplitterObserved = [bool]$observedOn.Profile7Splitter
    TwoDecoderInstancesObserved = ([int]$observedOn.DecoderInstances -ge 2)
    BlElPairingActive = ($observedOn.BlElPairing -eq 'Active')
    RpuPresent = ($observedOn.RpuState -eq 'Present')
    FelCompositionActive = ($observedOn.FelComposition -eq 'Active')
    HardwareSurfacesActive = ($observedOn.HardwareSurfaces -eq 'Active')
    NoDegradationReported = (@($observedOn.Degradation).Count -eq 0)
    ControlRunAgreesWithPrimary = ($felOnControl.PipelineState.Observed.FelComposition -eq 'Active')
    FelOffSuppressesComposition = ($observedOff.FelComposition -eq 'Inactive')
}

# The control must be pixel-identical. Anything else means the renderer is not
# deterministic at this timestamp and the A/B difference proves nothing.
$renderedChecks = [ordered]@{
    ControlIsPixelIdentical = ([long]$control.ChangedPixelCount -eq 0)
    AbTimestampsMatch = ([Math]::Abs([double]$felOn.Capture.ObservedTimestamp - [double]$felOff.Capture.ObservedTimestamp) -le 0.125)
    AbDiffersAtSameTimestamp = ([long]$ab.ChangedPixelCount -gt 0)
    AbCaptureHashesDiffer = ($felOn.Capture.Sha256 -ne $felOff.Capture.Sha256)
}

$scratchChecks = [ordered]@{
    FelOnZeroMediaScratch = ($felOn.ZeroMediaScratch.Result -eq 'Zero')
    FelOffZeroMediaScratch = ($felOff.ZeroMediaScratch.Result -eq 'Zero')
    ControlZeroMediaScratch = ($felOnControl.ZeroMediaScratch.Result -eq 'Zero')
    SourceUnmodified = ($felOn.SourceIdentityMatchesAfterRun -and $felOff.SourceIdentityMatchesAfterRun -and $felOnControl.SourceIdentityMatchesAfterRun)
}

$failed = [Collections.Generic.List[string]]::new()
foreach ($group in @(
    @{ Name = 'execution'; Checks = $executionChecks },
    @{ Name = 'rendered'; Checks = $renderedChecks },
    @{ Name = 'scratch'; Checks = $scratchChecks })) {
    foreach ($entry in $group.Checks.GetEnumerator()) {
        if (-not $entry.Value) { $failed.Add("$($group.Name).$($entry.Key)") }
    }
}
$gate = if ($failed.Count -eq 0) { 'supported' } else { 'unsupported' }

$summary = [ordered]@{
    SchemaVersion = 1
    IntegrationGate = $gate
    FailedChecks = [string[]]$failed.ToArray()
    Runtime = [ordered]@{
        MpvVersion = $runtime.mpv.version
        MpvCommit = $runtime.mpv.commit
        FelMergeCommit = $runtime.mpv.felMergeCommit
        CommitsAfterFelMerge = $runtime.mpv.commitsAfterFelMerge
        FfmpegVersion = $runtime.ffmpeg.version
        LibplaceboVersion = $runtime.libplacebo.version
        LibplaceboApi = $runtime.libplacebo.apiVersion
        ExecutableSha256 = $runtime.runtime.executable.sha256
        ConsoleLauncherSha256 = $runtime.runtime.consoleLauncher.sha256
        Provider = $runtime.provider.name
        ReleaseTag = $runtime.provider.releaseTag
        StableRuntimeIsolatedFrom = $runtime.stableRuntimeBeforeProbe.path
    }
    Checks = [ordered]@{
        Execution = $executionChecks
        Rendered = $renderedChecks
        Scratch = $scratchChecks
    }
    RenderedComparison = [ordered]@{
        Control = [ordered]@{
            Description = 'Two FEL-on runs, identical configuration, same authored timestamp.'
            ChangedPixelCount = [long]$control.ChangedPixelCount
            PixelCount = [long]$control.PixelCount
            MaximumChannelDelta = [long]$control.MaximumChannelDelta
        }
        EnhancementLayerAb = [ordered]@{
            Description = 'FEL-on against FEL-off, identical configuration, same authored timestamp.'
            ChangedPixelCount = [long]$ab.ChangedPixelCount
            PixelCount = [long]$ab.PixelCount
            ChangedPixelFraction = [double]$ab.ChangedPixelFraction
            MaximumChannelDelta = [long]$ab.MaximumChannelDelta
            SampleMaximum = [long]$ab.SampleMaximum
            MeanAbsoluteChannelError = [double]$ab.MeanAbsoluteChannelError
            BoundingBox = $ab.BoundingBox
        }
    }
    Runs = [ordered]@{
        FelOn = $felOn
        FelOnControl = $felOnControl
        FelOff = $felOff
    }
    Limitations = @(
        'Composition, BL/EL pairing, and the Profile 7 splitter are observed through this pinned build''s diagnostic log. mpv exposes no structured IPC property for them, so those three observations are version-pinned to the manifest runtime.',
        'The deterministic capture is a 1280x720 window screenshot of the rendered output, not a 3840x2160 frame dump. It proves that enabling the enhancement layer changes rendered pixels; it does not quantify the contribution at native resolution.',
        'The A/B comparison covers one authored timestamp with confirmed non-trivial NLQ metadata. It is not a whole-title fidelity measurement.',
        'Process write-byte counters bound writes by the mpv process. Writes by any other process are outside that bound and are covered only by the run-directory snapshot.'
    )
}

$json = $summary | ConvertTo-Json -Depth 40
$full = [IO.Path]::GetFullPath($OutputPath)
Set-Content -LiteralPath $full -Value $json -Encoding utf8NoBOM
Write-Host "Native Dolby Vision evidence summary: $gate ($full)"
if ($failed.Count -gt 0) { Write-Host ("Failed checks: " + ($failed -join ', ')) -ForegroundColor Yellow }
