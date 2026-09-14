#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [string]$RuntimeManifest = (Join-Path $PSScriptRoot '..\docs\native-dv-p7\runtime-manifest.json'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\.artifacts\native-dv\runs'),
    [string]$RunId = ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)),
    [ValidateSet('yes', 'no')][string]$EnhancementLayer = 'yes',
    [double]$StartSeconds = 0,
    [double]$DurationSeconds = 20,
    [Nullable[double]]$CaptureTimestamp,
    [ValidateSet('auto-safe', 'nvdec', 'd3d11va', 'no')][string]$HardwareDecoder = 'auto-safe',
    [ValidateSet('auto', 'd3d11', 'vulkan')][string]$GpuApi = 'auto',
    [ValidateSet('auto', 'd3d11', 'winvk')][string]$GpuContext = 'auto',
    [string]$FfprobePath,
    [string]$FfmpegPath,
    [string]$DoviToolPath = (Join-Path $PSScriptRoot '..\.artifacts\dv-tests\dovi_tool-2.3.3\dovi_tool.exe'),
    [switch]$SkipClassification,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Import-Module (Join-Path $PSScriptRoot 'NativeDvExperiment.psm1') -Force
if (-not ('NativeDvProcessIo' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

public sealed class NativeDvProcessIoResult
{
    public ulong ReadOperationCount { get; set; }
    public ulong WriteOperationCount { get; set; }
    public ulong OtherOperationCount { get; set; }
    public ulong ReadTransferBytes { get; set; }
    public ulong WriteTransferBytes { get; set; }
    public ulong OtherTransferBytes { get; set; }
}

public static class NativeDvProcessIo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    public static NativeDvProcessIoResult Read(Process process)
    {
        if (!GetProcessIoCounters(process.Handle, out IO_COUNTERS value))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new NativeDvProcessIoResult {
            ReadOperationCount = value.ReadOperationCount,
            WriteOperationCount = value.WriteOperationCount,
            OtherOperationCount = value.OtherOperationCount,
            ReadTransferBytes = value.ReadTransferCount,
            WriteTransferBytes = value.WriteTransferCount,
            OtherTransferBytes = value.OtherTransferCount
        };
    }
}
'@
}

function Get-NativeDvProcessIo {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)
    try { return [NativeDvProcessIo]::Read($Process) } catch { return $null }
}

function Write-NativeDvJson {
    param([Parameter(Mandatory = $true)]$Value, [Parameter(Mandatory = $true)][string]$Path, [int]$Depth = 30)
    $Value | ConvertTo-Json -Depth $Depth | Set-Content -LiteralPath $Path -Encoding utf8NoBOM
}

function Add-NativeDvEvent {
    param([string]$Phase, [string]$Event, $Data)
    $record = [ordered]@{
        TimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        Phase = $Phase
        Event = $Event
        Data = $Data
    }
    ($record | ConvertTo-Json -Depth 15 -Compress) | Add-Content -LiteralPath $script:eventsPath -Encoding utf8NoBOM
}

function Invoke-NativeDvCapturedProcess {
    param([string]$Executable, [string[]]$Arguments, [hashtable]$Environment = @{})
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $Executable
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) { $psi.Environment[$key] = [string]$Environment[$key] }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($psi)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $io = Get-NativeDvProcessIo -Process $process
    [pscustomobject]@{
        Executable = $Executable
        Arguments = [string[]]$Arguments
        Environment = $Environment
        ExitCode = $process.ExitCode
        ElapsedMilliseconds = $watch.ElapsedMilliseconds
        Io = $io
        StandardOutput = $stdoutTask.GetAwaiter().GetResult()
        StandardError = $stderrTask.GetAwaiter().GetResult()
    }
}

function Invoke-NativeDvRpuProbe {
    param(
        [string]$Ffmpeg,
        [string]$DoviTool,
        [string]$Source,
        [double]$SeekSeconds,
        [string]$RpuOutput,
        [hashtable]$Environment
    )
    $culture = [Globalization.CultureInfo]::InvariantCulture
    $ffmpegArgs = @('-hide_banner', '-loglevel', 'error', '-ss', $SeekSeconds.ToString('0.###', $culture), '-i', $Source, '-map', '0:v:0', '-c:v', 'copy', '-f', 'hevc', 'pipe:1')
    $doviArgs = @('extract-rpu', '--limit', '120', '--input', '-', '--rpu-out', $RpuOutput)
    $ffmpegPsi = [Diagnostics.ProcessStartInfo]::new()
    $ffmpegPsi.FileName = $Ffmpeg
    $ffmpegPsi.UseShellExecute = $false
    $ffmpegPsi.CreateNoWindow = $true
    $ffmpegPsi.RedirectStandardOutput = $true
    $ffmpegPsi.RedirectStandardError = $true
    foreach ($argument in $ffmpegArgs) { [void]$ffmpegPsi.ArgumentList.Add($argument) }
    $doviPsi = [Diagnostics.ProcessStartInfo]::new()
    $doviPsi.FileName = $DoviTool
    $doviPsi.UseShellExecute = $false
    $doviPsi.CreateNoWindow = $true
    $doviPsi.RedirectStandardInput = $true
    $doviPsi.RedirectStandardOutput = $true
    $doviPsi.RedirectStandardError = $true
    foreach ($argument in $doviArgs) { [void]$doviPsi.ArgumentList.Add($argument) }
    foreach ($key in $Environment.Keys) {
        $ffmpegPsi.Environment[$key] = [string]$Environment[$key]
        $doviPsi.Environment[$key] = [string]$Environment[$key]
    }

    $consumer = [Diagnostics.Process]::Start($doviPsi)
    $producer = [Diagnostics.Process]::Start($ffmpegPsi)
    $producerError = $producer.StandardError.ReadToEndAsync()
    $consumerOutput = $consumer.StandardOutput.ReadToEndAsync()
    $consumerError = $consumer.StandardError.ReadToEndAsync()
    $copyError = $null
    try {
        $producer.StandardOutput.BaseStream.CopyTo($consumer.StandardInput.BaseStream)
    } catch {
        $copyError = $_.Exception.Message
    } finally {
        $consumer.StandardInput.Close()
    }
    if (-not $consumer.WaitForExit(60000)) { $consumer.Kill($true); throw 'dovi_tool RPU probe timed out.' }
    if (-not $producer.WaitForExit(10000)) { $producer.Kill($true); $producer.WaitForExit() }
    $producerIo = Get-NativeDvProcessIo -Process $producer
    $consumerIo = Get-NativeDvProcessIo -Process $consumer
    [pscustomobject]@{
        Ffmpeg = [pscustomobject]@{ Executable = $Ffmpeg; Arguments = $ffmpegArgs; ExitCode = $producer.ExitCode; Io = $producerIo; StandardError = $producerError.GetAwaiter().GetResult() }
        DoviTool = [pscustomobject]@{ Executable = $DoviTool; Arguments = $doviArgs; ExitCode = $consumer.ExitCode; Io = $consumerIo; StandardOutput = $consumerOutput.GetAwaiter().GetResult(); StandardError = $consumerError.GetAwaiter().GetResult() }
        PipeCopyError = $copyError
    }
}

function Get-NativeDvIpcSnapshot {
    param([scriptblock]$Ipc)
    $values = [ordered]@{}
    foreach ($property in @(
        'pid', 'mpv-version', 'path', 'duration', 'time-pos', 'pause', 'seeking', 'paused-for-cache',
        'hwdec-current', 'gpu-api', 'gpu-context', 'vf', 'video-params', 'video-out-params', 'track-list',
        'container-fps', 'display-fps', 'estimated-display-fps', 'frame-drop-count',
        'decoder-frame-drop-count', 'mistimed-frame-count', 'vo-delayed-frame-count'
    )) {
        $response = & $Ipc @('get_property', $property)
        $values[$property] = $response
    }
    [pscustomobject]$values
}

function Get-NativeDvMetricSample {
    param([int]$ProcessId, [string]$Phase, [string]$NvidiaSmi)
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $io = Get-NativeDvProcessIo -Process $process
    $processGpu = $null
    try {
        $counter = Get-Counter -Counter @(
            "\GPU Process Memory(pid_${ProcessId}_*)\Dedicated Usage",
            "\GPU Process Memory(pid_${ProcessId}_*)\Shared Usage",
            "\GPU Engine(pid_${ProcessId}_*_engtype_*)\Utilization Percentage"
        ) -ErrorAction Stop
        $counterSamples = @($counter.CounterSamples)
        $dedicated = @($counterSamples | Where-Object Path -Like '*\dedicated usage')
        $shared = @($counterSamples | Where-Object Path -Like '*\shared usage')
        $decode = @($counterSamples | Where-Object Path -Like '*engtype_videodecode*')
        $threeD = @($counterSamples | Where-Object Path -Like '*engtype_3d*')
        $processGpu = [pscustomobject]@{
            DedicatedBytes = [long](($dedicated | Measure-Object CookedValue -Sum).Sum)
            SharedBytes = [long](($shared | Measure-Object CookedValue -Sum).Sum)
            VideoDecodePercent = [double](($decode | Measure-Object CookedValue -Sum).Sum)
            ThreeDPercent = [double](($threeD | Measure-Object CookedValue -Sum).Sum)
        }
    } catch { $processGpu = $null }
    $gpu = $null
    if ($NvidiaSmi) {
        try {
            $line = & $NvidiaSmi --query-gpu=utilization.gpu,utilization.decoder,memory.used,memory.free --format=csv,noheader,nounits 2>$null | Select-Object -First 1
            if ($line) {
                $parts = @($line -split ',' | ForEach-Object { $_.Trim() })
                $gpu = [pscustomobject]@{ GpuUtilizationPercent = [double]$parts[0]; DecoderUtilizationPercent = [double]$parts[1]; GpuMemoryUsedMiB = [double]$parts[2]; GpuMemoryFreeMiB = [double]$parts[3] }
            }
        } catch { $gpu = $null }
    }
    [pscustomobject]@{
        TimestampUtc = (Get-Date).ToUniversalTime().ToString('o')
        Phase = $Phase
        Pid = $ProcessId
        CpuTotalSeconds = [double]$process.CPU
        WorkingSetBytes = [long]$process.WorkingSet64
        PrivateBytes = [long]$process.PrivateMemorySize64
        PagedMemoryBytes = [long]$process.PagedMemorySize64
        IoReadOperationCount = if ($io) { [uint64]$io.ReadOperationCount } else { $null }
        IoWriteOperationCount = if ($io) { [uint64]$io.WriteOperationCount } else { $null }
        IoReadTransferBytes = if ($io) { [uint64]$io.ReadTransferBytes } else { $null }
        IoWriteTransferBytes = if ($io) { [uint64]$io.WriteTransferBytes } else { $null }
        ProcessGpuDedicatedBytes = if ($processGpu) { $processGpu.DedicatedBytes } else { $null }
        ProcessGpuSharedBytes = if ($processGpu) { $processGpu.SharedBytes } else { $null }
        ProcessGpuVideoDecodePercent = if ($processGpu) { $processGpu.VideoDecodePercent } else { $null }
        ProcessGpuThreeDPercent = if ($processGpu) { $processGpu.ThreeDPercent } else { $null }
        GpuUtilizationPercent = if ($gpu) { $gpu.GpuUtilizationPercent } else { $null }
        DecoderUtilizationPercent = if ($gpu) { $gpu.DecoderUtilizationPercent } else { $null }
        GpuMemoryUsedMiB = if ($gpu) { $gpu.GpuMemoryUsedMiB } else { $null }
        GpuMemoryFreeMiB = if ($gpu) { $gpu.GpuMemoryFreeMiB } else { $null }
    }
}

function Wait-NativeDvSeek {
    param([scriptblock]$Ipc, [double]$Target, [int]$TimeoutMilliseconds = 15000)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 50
        $seeking = & $Ipc @('get_property', 'seeking')
        $position = & $Ipc @('get_property', 'time-pos')
        if (-not $seeking -and $null -ne $position -and [Math]::Abs([double]$position - $Target) -le 0.125) {
            return [pscustomobject]@{ TargetSeconds = $Target; ObservedSeconds = [double]$position; RecoveryMilliseconds = $watch.ElapsedMilliseconds; WithinTolerance = $true }
        }
    } while ($watch.ElapsedMilliseconds -lt $TimeoutMilliseconds)
    [pscustomobject]@{ TargetSeconds = $Target; ObservedSeconds = $position; RecoveryMilliseconds = $watch.ElapsedMilliseconds; WithinTolerance = $false }
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$manifestPath = (Resolve-Path -LiteralPath $RuntimeManifest).Path
$output = [IO.Path]::GetFullPath($OutputRoot)
$run = [IO.Path]::GetFullPath((Join-Path $output $RunId))
if (Test-Path -LiteralPath $run) { throw "Run directory already exists: $run" }
foreach ($directory in @($run, (Join-Path $run 'state'), (Join-Path $run 'temp'), (Join-Path $run 'logs'), (Join-Path $run 'captures'))) {
    [void][IO.Directory]::CreateDirectory($directory)
}
$script:eventsPath = Join-Path $run 'events.jsonl'
$pipeName = "adaptive-media-native-dv-$([guid]::NewGuid().ToString('N'))"
$invocation = New-NativeDvInvocation -RuntimeManifest $manifestPath -SourcePath $source -RunDirectory $run `
    -EnhancementLayer ($EnhancementLayer -eq 'yes') -StartSeconds $StartSeconds -DurationSeconds $DurationSeconds `
    -PipeName $pipeName -HardwareDecoder $HardwareDecoder -GpuApi $GpuApi -GpuContext $GpuContext
$sourceIdentity = Get-NativeDvSourceIdentity -Path $source
$sourceIdentityPath = Join-Path $run 'source-identity-before.json'
Write-NativeDvJson -Value $sourceIdentity -Path $sourceIdentityPath
$stablePath = 'C:\mpv\mpv.exe'
$stable = if (Test-Path -LiteralPath $stablePath) {
    $item = Get-Item -LiteralPath $stablePath
    [pscustomobject]@{ Path = $stablePath; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc; Sha256 = (Get-FileHash -LiteralPath $stablePath -Algorithm SHA256).Hash.ToLowerInvariant() }
} else { $null }
$runManifest = [ordered]@{
    SchemaVersion = 1
    RunId = $RunId
    CreatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    PlanOnly = [bool]$PlanOnly
    Source = $sourceIdentity
    RuntimeManifest = $manifestPath
    Invocation = $invocation
    StableRuntimeBefore = $stable
    ProcessPath = $env:PATH
    Host = [ordered]@{ PowerShell = $PSVersionTable.PSVersion.ToString(); OS = [Environment]::OSVersion.VersionString; LogicalProcessors = [Environment]::ProcessorCount }
}
Write-NativeDvJson -Value $runManifest -Path (Join-Path $run 'manifest.json')
$filesystemBefore = Get-NativeDvFileSnapshot -Path $run
Write-NativeDvJson -Value @($filesystemBefore) -Path (Join-Path $run 'filesystem-before.json')
if ($PlanOnly) {
    Write-Output $run
    exit 0
}

$environment = @{ TEMP = $invocation.Environment.TEMP; TMP = $invocation.Environment.TMP }
$classification = $null
if (-not $SkipClassification) {
    $resolvedFfprobe = if ($FfprobePath) { (Resolve-Path -LiteralPath $FfprobePath).Path } else { (Get-Command ffprobe -ErrorAction Stop).Source }
    $resolvedFfmpeg = if ($FfmpegPath) { (Resolve-Path -LiteralPath $FfmpegPath).Path } else { (Get-Command ffmpeg -ErrorAction Stop).Source }
    $resolvedDovi = (Resolve-Path -LiteralPath $DoviToolPath).Path
    Add-NativeDvEvent -Phase 'classification' -Event 'started' -Data $null
    $ffprobeArgs = @('-v', 'error', '-show_streams', '-show_format', '-of', 'json', '--', $source)
    $probe = Invoke-NativeDvCapturedProcess -Executable $resolvedFfprobe -Arguments $ffprobeArgs -Environment $environment
    if ($probe.ExitCode -ne 0) { throw "ffprobe classification failed: $($probe.StandardError)" }
    $probe.StandardOutput | Set-Content -LiteralPath (Join-Path $run 'classification-ffprobe.json') -Encoding utf8NoBOM
    $rpuPath = Join-Path $run 'state\classification.rpu.bin'
    $rpuProbe = Invoke-NativeDvRpuProbe -Ffmpeg $resolvedFfmpeg -DoviTool $resolvedDovi -Source $source -SeekSeconds $StartSeconds -RpuOutput $rpuPath -Environment $environment
    if ($rpuProbe.DoviTool.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $rpuPath)) {
        throw "dovi_tool RPU probe failed: $($rpuProbe.DoviTool.StandardError)"
    }
    $summary = Invoke-NativeDvCapturedProcess -Executable $resolvedDovi -Arguments @('info', '--summary', '--input', $rpuPath) -Environment $environment
    if ($summary.ExitCode -ne 0) { throw "dovi_tool summary failed: $($summary.StandardError)" }
    $emptyRuntime = Get-NativeDvRuntimeEvidence -LogText '' -RequestedEnhancementLayer ($EnhancementLayer -eq 'yes') -RequestedHardwareDecoder $HardwareDecoder
    $melFel = Get-NativeDvMelFelClassification -DoviToolSummary ($summary.StandardOutput + $summary.StandardError) -RuntimeEvidence $emptyRuntime
    $classification = [ordered]@{
        Ffprobe = $probe
        RpuProbe = $rpuProbe
        DoviToolSummary = $summary
        MelFel = $melFel.MelFel
        Evidence = $melFel.Evidence
        RpuBytes = (Get-Item -LiteralPath $rpuPath).Length
        Tools = @(
            [ordered]@{ Name = 'ffprobe'; Path = $resolvedFfprobe; Sha256 = (Get-FileHash -LiteralPath $resolvedFfprobe -Algorithm SHA256).Hash.ToLowerInvariant() },
            [ordered]@{ Name = 'ffmpeg'; Path = $resolvedFfmpeg; Sha256 = (Get-FileHash -LiteralPath $resolvedFfmpeg -Algorithm SHA256).Hash.ToLowerInvariant() },
            [ordered]@{ Name = 'dovi_tool'; Path = $resolvedDovi; Sha256 = (Get-FileHash -LiteralPath $resolvedDovi -Algorithm SHA256).Hash.ToLowerInvariant() }
        )
    }
    Write-NativeDvJson -Value $classification -Path (Join-Path $run 'classification.json')
    Add-NativeDvEvent -Phase 'classification' -Event 'completed' -Data @{ MelFel = $classification.MelFel; RpuBytes = $classification.RpuBytes }
    $identityAfterClassification = Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity
    Write-NativeDvJson -Value $identityAfterClassification -Path (Join-Path $run 'source-identity-after-classification.json')
    if (-not $identityAfterClassification.Matches) { throw 'Source identity changed during classification.' }
}

$process = $null
$mpvNativeProcess = $null
$finalProcessIo = $null
$pipe = $null
$reader = $null
$writer = $null
$metrics = [Collections.Generic.List[object]]::new()
$snapshots = [ordered]@{}
$seeks = [Collections.Generic.List[object]]::new()
$capture = $null
$playbackError = $null
try {
    Add-NativeDvEvent -Phase 'launch' -Event 'started' -Data @{ EnhancementLayer = $EnhancementLayer; HardwareDecoder = $HardwareDecoder; GpuApi = $GpuApi; GpuContext = $GpuContext }
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $invocation.Executable
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $false
    foreach ($argument in $invocation.Arguments) { [void]$psi.ArgumentList.Add($argument) }
    $psi.Environment['TEMP'] = $invocation.Environment.TEMP
    $psi.Environment['TMP'] = $invocation.Environment.TMP
    $process = [Diagnostics.Process]::Start($psi)
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    $pipe.Connect(30000)
    $reader = [IO.StreamReader]::new($pipe, [Text.UTF8Encoding]::new($false))
    $writer = [IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false))
    $writer.AutoFlush = $true
    $script:requestId = 0
    $ipc = {
        param([object[]]$Command)
        $script:requestId += 1
        $writer.WriteLine((@{ command = $Command; request_id = $script:requestId } | ConvertTo-Json -Compress -Depth 8))
        do {
            $readTask = $reader.ReadLineAsync()
            if (-not $readTask.Wait(10000)) { throw "IPC response timed out: $($Command -join ' ')" }
            $line = $readTask.Result
            if (-not $line) { throw 'mpv IPC closed.' }
            $reply = $line | ConvertFrom-Json
            $hasRequestId = $reply.PSObject.Properties.Name -contains 'request_id'
        } while (-not $hasRequestId -or $reply.request_id -ne $script:requestId)
        if ($reply.error -ne 'success') { return $null }
        if ($reply.PSObject.Properties.Name -contains 'data') { return $reply.data }
        return $null
    }
    $readyWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Start-Sleep -Milliseconds 100
        $videoParams = & $ipc @('get_property', 'video-params')
    } while (-not $videoParams -and $readyWatch.ElapsedMilliseconds -lt 30000)
    if (-not $videoParams) { throw 'mpv did not expose video-params within 30 seconds.' }
    $snapshots.Launch = Get-NativeDvIpcSnapshot -Ipc $ipc
    $mpvPid = [int]$snapshots.Launch.pid
    $mpvNativeProcess = Get-Process -Id $mpvPid -ErrorAction Stop
    [void]$mpvNativeProcess.Handle
    $nvidia = (Get-Command nvidia-smi -ErrorAction SilentlyContinue).Source
    $metrics.Add((Get-NativeDvMetricSample -ProcessId $mpvPid -Phase 'launch' -NvidiaSmi $nvidia))
    Add-NativeDvEvent -Phase 'launch' -Event 'ready' -Data @{ Pid = $mpvPid; Milliseconds = $readyWatch.ElapsedMilliseconds }
    Write-NativeDvJson -Value (Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity) -Path (Join-Path $run 'source-identity-after-launch.json')

    $target = if ($null -ne $CaptureTimestamp) { [double]$CaptureTimestamp } else { $StartSeconds }
    [void](& $ipc @('set_property', 'pause', $true))
    [void](& $ipc @('seek', $target, 'absolute+exact'))
    $captureSeek = Wait-NativeDvSeek -Ipc $ipc -Target $target
    $seeks.Add($captureSeek)
    if (-not $captureSeek.WithinTolerance) { throw "Capture seek did not settle at $target seconds." }
    Start-Sleep -Milliseconds 500
    $capturePath = Join-Path $run "captures\enhancement-$EnhancementLayer.png"
    [void](& $ipc @('screenshot-to-file', $capturePath, 'window'))
    $captureWait = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $capturePath) -and $captureWait.ElapsedMilliseconds -lt 10000) { Start-Sleep -Milliseconds 50 }
    if (-not (Test-Path -LiteralPath $capturePath)) { throw 'mpv did not create the deterministic capture.' }
    $capture = [ordered]@{
        Path = $capturePath
        Bytes = (Get-Item -LiteralPath $capturePath).Length
        Sha256 = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
        RequestedTimestamp = $target
        ObservedTimestamp = [double](& $ipc @('get_property', 'time-pos'))
        Mode = 'window'
    }
    Add-NativeDvEvent -Phase 'capture' -Event 'completed' -Data $capture

    [void](& $ipc @('set_property', 'pause', $false))
    $sustainedWatch = [Diagnostics.Stopwatch]::StartNew()
    while ($sustainedWatch.Elapsed.TotalSeconds -lt $DurationSeconds) {
        Start-Sleep -Seconds 1
        $metrics.Add((Get-NativeDvMetricSample -ProcessId $mpvPid -Phase 'sustained' -NvidiaSmi $nvidia))
    }
    $snapshots.Sustained = Get-NativeDvIpcSnapshot -Ipc $ipc
    Write-NativeDvJson -Value (Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity) -Path (Join-Path $run 'source-identity-after-sustained.json')

    [void](& $ipc @('set_property', 'pause', $true))
    Start-Sleep -Seconds 1
    $metrics.Add((Get-NativeDvMetricSample -ProcessId $mpvPid -Phase 'pause' -NvidiaSmi $nvidia))
    $snapshots.Pause = Get-NativeDvIpcSnapshot -Ipc $ipc
    [void](& $ipc @('set_property', 'pause', $false))
    Start-Sleep -Seconds 1
    $metrics.Add((Get-NativeDvMetricSample -ProcessId $mpvPid -Phase 'resume' -NvidiaSmi $nvidia))
    $snapshots.Resume = Get-NativeDvIpcSnapshot -Ipc $ipc
    Write-NativeDvJson -Value (Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity) -Path (Join-Path $run 'source-identity-after-pause-resume.json')

    [void](& $ipc @('set_property', 'pause', $true))
    foreach ($seekTarget in @(
        ($target + 30),
        ($target + 5),
        ($target + 90),
        ($target + 10),
        $target
    )) {
        [void](& $ipc @('seek', $seekTarget, 'absolute+exact'))
        $seekResult = Wait-NativeDvSeek -Ipc $ipc -Target $seekTarget
        $seeks.Add($seekResult)
        $metrics.Add((Get-NativeDvMetricSample -ProcessId $mpvPid -Phase 'seek' -NvidiaSmi $nvidia))
        Add-NativeDvEvent -Phase 'seek' -Event 'settled' -Data $seekResult
    }
    $snapshots.Seeks = Get-NativeDvIpcSnapshot -Ipc $ipc
    Write-NativeDvJson -Value (Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity) -Path (Join-Path $run 'source-identity-after-seeks.json')
    [void](& $ipc @('quit'))
    if (-not $process.WaitForExit(15000)) { throw 'mpv did not exit after IPC quit.' }
    $finalProcessIo = Get-NativeDvProcessIo -Process $mpvNativeProcess
    Add-NativeDvEvent -Phase 'exit' -Event 'completed' -Data @{ ExitCode = $process.ExitCode }
} catch {
    $playbackError = $_
    Add-NativeDvEvent -Phase 'error' -Event 'exception' -Data @{ Message = $_.Exception.Message; Type = $_.Exception.GetType().FullName }
} finally {
    # Read the counters while the cached handle is still valid, before the finally
    # block's cleanup can close it, and before a kill discards the process.
    if (-not $finalProcessIo -and $mpvNativeProcess) { $finalProcessIo = Get-NativeDvProcessIo -Process $mpvNativeProcess }
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    if ($pipe) { $pipe.Dispose() }
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true); $process.WaitForExit() } catch {}
    }
    if ($metrics.Count -gt 0) { $metrics.ToArray() | Export-Csv -LiteralPath (Join-Path $run 'metrics.csv') -NoTypeInformation -Encoding utf8NoBOM }
    Write-NativeDvJson -Value $snapshots -Path (Join-Path $run 'ipc-snapshots.json')
    Write-NativeDvJson -Value @($seeks.ToArray()) -Path (Join-Path $run 'seeks.json')
    if ($capture) { Write-NativeDvJson -Value $capture -Path (Join-Path $run 'capture.json') }
    if ($finalProcessIo) { Write-NativeDvJson -Value $finalProcessIo -Path (Join-Path $run 'process-io-final.json') }
    $logText = if (Test-Path -LiteralPath $invocation.LogPath) { Get-Content -LiteralPath $invocation.LogPath -Raw } else { '' }
    $runtimeEvidence = Get-NativeDvRuntimeEvidence -LogText $logText -RequestedEnhancementLayer ($EnhancementLayer -eq 'yes') -RequestedHardwareDecoder $HardwareDecoder
    $summaryText = if ($classification) { [string]$classification.DoviToolSummary.StandardOutput + [string]$classification.DoviToolSummary.StandardError } else { '' }
    $finalClassification = Get-NativeDvMelFelClassification -DoviToolSummary $summaryText -RuntimeEvidence $runtimeEvidence
    Write-NativeDvJson -Value $runtimeEvidence -Path (Join-Path $run 'runtime-evidence.json')
    Write-NativeDvJson -Value $finalClassification -Path (Join-Path $run 'classification-final.json')
    $sourceAfter = Test-NativeDvSourceIdentity -Path $source -ExpectedIdentity $sourceIdentity
    Write-NativeDvJson -Value $sourceAfter -Path (Join-Path $run 'source-identity-after.json')
    $filesystemAfter = Get-NativeDvFileSnapshot -Path $run
    Write-NativeDvJson -Value @($filesystemAfter) -Path (Join-Path $run 'filesystem-after.json')
    $filesystemDelta = Compare-NativeDvFileSnapshot -Before $filesystemBefore -After $filesystemAfter
    Write-NativeDvJson -Value $filesystemDelta -Path (Join-Path $run 'filesystem-delta.json')
    $stableAfter = if (Test-Path -LiteralPath $stablePath) {
        $item = Get-Item -LiteralPath $stablePath
        [pscustomobject]@{ Path = $stablePath; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc; Sha256 = (Get-FileHash -LiteralPath $stablePath -Algorithm SHA256).Hash.ToLowerInvariant() }
    } else { $null }
    Write-NativeDvJson -Value $stableAfter -Path (Join-Path $run 'stable-runtime-after.json')
    $launchSnapshot = if ($snapshots.Contains('Launch')) { $snapshots.Launch } else { $null }
    $pipelineState = Get-NativeDvPipelineState -RuntimeEvidence $runtimeEvidence -Invocation $invocation `
        -IpcSnapshot $launchSnapshot -FilesystemDelta $filesystemDelta -ProcessIo $finalProcessIo `
        -SourceClassification $finalClassification.MelFel
    Write-NativeDvJson -Value $pipelineState -Path (Join-Path $run 'pipeline-state.json')
    $zeroScratch = Test-NativeDvZeroMediaScratch -FilesystemDelta $filesystemDelta -ProcessIo $finalProcessIo
    Write-NativeDvJson -Value $zeroScratch -Path (Join-Path $run 'zero-media-scratch.json')
    $result = [ordered]@{
        RunId = $RunId
        RunDirectory = $run
        ExitCode = if ($process -and $process.HasExited) { $process.ExitCode } else { $null }
        Error = if ($playbackError) { $playbackError.Exception.Message } else { $null }
        SourceIdentityMatches = $sourceAfter.Matches
        StableRuntimeMatches = ($null -eq $stable -and $null -eq $stableAfter) -or ($stable -and $stableAfter -and $stable.Sha256 -eq $stableAfter.Sha256 -and $stable.Length -eq $stableAfter.Length -and $stable.LastWriteTimeUtc -eq $stableAfter.LastWriteTimeUtc)
        Filesystem = $filesystemDelta
        RuntimeEvidence = $runtimeEvidence
        PipelineState = $pipelineState
        ZeroMediaScratch = $zeroScratch
        MelFel = $finalClassification.MelFel
        Capture = $capture
        FinalProcessIo = $finalProcessIo
    }
    Write-NativeDvJson -Value $result -Path (Join-Path $run 'result.json')
}

if ($playbackError) { throw $playbackError }
Write-Output $run
