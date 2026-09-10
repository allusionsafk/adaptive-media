#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$InstallInno
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $root '.artifacts/ci-tools'
}
$Destination = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null

function Get-VerifiedDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Sha256
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        Invoke-WebRequest -Uri $Uri -OutFile $Path
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        throw "Download digest mismatch for $Path. Expected $Sha256, found $actual."
    }
}

$ffmpegArchive = Join-Path $Destination 'ffmpeg-7.1-full_build.zip'
Get-VerifiedDownload `
    -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/7.1/ffmpeg-7.1-full_build.zip' `
    -Path $ffmpegArchive `
    -Sha256 'FA074A46B7BB862E37AA27CCD0522D22A1FE90AEC3A950C8E0C0C8622E0A03D1'

$ffmpegRoot = Join-Path $Destination 'ffmpeg-7.1'
if (-not (Test-Path -LiteralPath $ffmpegRoot -PathType Container)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ffmpegArchive)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($name) -or $name -match '(^|\\)\.\.(\\|$)' -or $name -match ':') {
                throw "Unsafe FFmpeg archive entry: $($entry.FullName)"
            }
        }
    }
    finally {
        $archive.Dispose()
    }
    Expand-Archive -LiteralPath $ffmpegArchive -DestinationPath $ffmpegRoot
}

$ffmpeg = Get-ChildItem -LiteralPath $ffmpegRoot -Filter ffmpeg.exe -Recurse | Select-Object -First 1
$ffprobe = Get-ChildItem -LiteralPath $ffmpegRoot -Filter ffprobe.exe -Recurse | Select-Object -First 1
if (-not $ffmpeg -or -not $ffprobe) { throw 'Pinned FFmpeg archive did not contain ffmpeg.exe and ffprobe.exe.' }
if ((& $ffmpeg.FullName -version | Select-Object -First 1) -notmatch '^ffmpeg version 7\.1(?:\s|-)') {
    throw 'Prepared FFmpeg does not report version 7.1.'
}

$innoDirectory = $null
if ($InstallInno) {
    $innoInstaller = Join-Path $Destination 'innosetup-6.7.3.exe'
    Get-VerifiedDownload `
        -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' `
        -Path $innoInstaller `
        -Sha256 '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732'
    $innoDirectory = Join-Path $Destination 'inno-6.7.3'
    $iscc = Join-Path $innoDirectory 'ISCC.exe'
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) {
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=$innoDirectory")
        $process = Start-Process -FilePath $innoInstaller -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
        if ($process.ExitCode -ne 0) { throw "Inno Setup installation failed with exit code $($process.ExitCode)." }
    }
    if (-not (Test-Path -LiteralPath $iscc -PathType Leaf)) { throw 'Pinned Inno Setup installation did not produce ISCC.exe.' }
}

[pscustomobject]@{
    FfmpegDirectory = $ffmpeg.Directory.FullName
    Ffmpeg = $ffmpeg.FullName
    Ffprobe = $ffprobe.FullName
    InnoDirectory = $innoDirectory
}
