#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $root 'scripts\NativeDvExperiment.psm1'

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "ASSERT: $Message" }
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
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

    Write-Host "Native Dolby Vision experiment contract: PASS ($($PSVersionTable.PSVersion))"
} finally {
    $fullTemp = [IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($fullTemp.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTemp).StartsWith('adaptive-media-native-dv-test-', [StringComparison]::Ordinal)) {
        Remove-Item -LiteralPath $fullTemp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
