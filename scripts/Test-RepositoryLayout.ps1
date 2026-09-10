#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$required = @(
    'src/AdaptiveMedia.App/AdaptiveMedia.App.csproj',
    'tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj',
    'tests/DolbyVisionTests/DolbyVisionTests.csproj',
    'tests/DolbyVisionExecutionTests/DolbyVisionExecutionTests.csproj',
    'installer/AdaptiveMedia.iss',
    'payload/AdaptiveMedia.Engine.ps1',
    'docs/DOLBY-VISION-CONTRACT.md',
    'docs/MIGRATION-PROVENANCE.md'
)

foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Required standalone path is missing: $relative"
    }
}

$tracked = @(& git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked files.' }
foreach ($prefix in @('adaptive-media/', 'source/')) {
    $matches = @($tracked | Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    if ($matches.Count -ne 0) {
        throw "Obsolete top-level path remains tracked: $($matches -join ', ')"
    }
}

$activePaths = @('src', 'tests', 'installer', 'payload', 'scripts')
$activeFiles = @(& git -C $root ls-files -- $activePaths)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate active repository files.' }
$oldLayout = [regex]'(?i)(?:^|[\s''"`])(adaptive-media[\\/]|source[\\/](?:src|tests|scripts|installer|payload)[\\/])'
$oldRepository = [regex]'(?i)allusionsafk/localai-windows-starter'
$violations = [Collections.Generic.List[string]]::new()

foreach ($relative in $activeFiles) {
    if ($relative -eq 'scripts/Test-RepositoryLayout.ps1') { continue }
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $content = [IO.File]::ReadAllText($path)
    if ($oldLayout.IsMatch($content) -or $oldRepository.IsMatch($content)) {
        $violations.Add($relative)
    }
}

if ($violations.Count -ne 0) {
    throw "Active files still depend on the shared repository layout: $($violations -join ', ')"
}

Write-Host "Standalone repository layout: PASS ($($tracked.Count) tracked files)"
