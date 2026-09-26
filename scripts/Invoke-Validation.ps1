#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$BuildInstaller,
    [switch]$InstallerSmokeTest
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Invoke-Gate {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    Invoke-Gate 'Adaptive Media assertions' {
        dotnet run --project tests/AdaptiveMedia.Tests/AdaptiveMedia.Tests.csproj -c Release
    }
    Invoke-Gate 'Settings assertions' {
        dotnet run --project tests/SettingsTests/SettingsTests.csproj -c Release
    }
    Invoke-Gate 'Cinema Boost assertions' {
        dotnet run --project tests/CinemaBoostTests/CinemaBoostTests.csproj -c Release
    }
    Invoke-Gate 'Public identity assertions' {
        & (Join-Path $root 'tests/Test-PublicIdentity.ps1')
    }
    Invoke-Gate 'Dolby Vision semantic assertions' {
        dotnet run --project tests/DolbyVisionTests/DolbyVisionTests.csproj -c Release
    }
    Invoke-Gate 'Packaging safety' {
        & (Join-Path $root 'tests/Test-Packaging.ps1')
    }
    Invoke-Gate 'Historical reconstruction safety' {
        & (Join-Path $root 'tests/Test-Reconstruction.ps1')
    }
    Invoke-Gate 'PowerShell syntax' {
        & (Join-Path $root 'scripts/Test-PowerShellSyntax.ps1')
    }
    Invoke-Gate 'Standalone repository layout' {
        & (Join-Path $root 'scripts/Test-RepositoryLayout.ps1')
    }
    Invoke-Gate 'Native Dolby Vision experiment contract' {
        & (Join-Path $root 'tests/Test-NativeDvExperiment.ps1')
    }
    Invoke-Gate 'Dolby Vision real execution' {
        & (Join-Path $root 'tests/Run-DolbyVisionExecutionTests.ps1')
    }
    Invoke-Gate 'Application restore' {
        dotnet restore src/AdaptiveMedia.App/AdaptiveMedia.App.csproj
    }
    Invoke-Gate 'Application build' {
        dotnet build src/AdaptiveMedia.App/AdaptiveMedia.App.csproj -c Release --no-restore
    }
    if ($BuildInstaller) {
        Invoke-Gate 'Application and installer build' {
            $parameters = @{ Clean = $true }
            if ($InstallerSmokeTest) { $parameters.SmokeTest = $true }
            & (Join-Path $root 'scripts/Build-Dev.ps1') @parameters
        }
    }
    Invoke-Gate 'Git whitespace check' {
        git diff --check
    }
}
finally {
    Pop-Location
}
