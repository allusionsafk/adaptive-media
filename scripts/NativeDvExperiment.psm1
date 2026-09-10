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
        [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9._-]+$')][string]$PipeName
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
        '--force-window=yes',
        '--pause=yes',
        "--start=$($StartSeconds.ToString('0.###', $culture))",
        "--length=$($DurationSeconds.ToString('0.###', $culture))",
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
    [pscustomobject]@{
        Success = ($media.Count -eq 0 -and $unknown.Count -eq 0)
        Changes = [object[]]$changes.ToArray()
        BoundedState = $bounded
        MediaPayload = $media
        Unknown = $unknown
        BoundedStateBytes = [long](($bounded | Measure-Object -Property AfterLength -Sum).Sum)
        MediaPayloadBytes = [long](($media | Measure-Object -Property AfterLength -Sum).Sum)
        UnknownBytes = [long](($unknown | Measure-Object -Property AfterLength -Sum).Sum)
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

Export-ModuleMember -Function @(
    'New-NativeDvInvocation',
    'Get-NativeDvFileSnapshot',
    'Compare-NativeDvFileSnapshot',
    'Test-NativeDvMediaArtifact',
    'Get-NativeDvSourceIdentity',
    'Test-NativeDvSourceIdentity'
)
