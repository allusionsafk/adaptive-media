#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$mainPath = Join-Path $root 'src/AdaptiveMedia.App/MainWindow.xaml'
$settingsPath = Join-Path $root 'src/AdaptiveMedia.App/SettingsWindow.xaml'
$main = [IO.File]::ReadAllText($mainPath)
$settings = [IO.File]::ReadAllText($settingsPath)

$assertions = 0
function Assert-True {
    param([bool]$Condition, [string]$Message)
    $script:assertions++
    if (-not $Condition) { throw $Message }
}

Assert-True ($main.Contains('Title="DemiMedia"')) 'Main window must use the DemiMedia public product name.'
Assert-True ($main.Contains('Text="DemiMedia"')) 'Main visible header must use the DemiMedia public product name.'
Assert-True ($main.Contains('Text="Development build"')) 'Development screenshot build must identify itself as a development build.'
Assert-True (-not $main.Contains('Text="v0.4.0-rc1"')) 'Current development UI must not present the historical rc1 as its current version.'
Assert-True ($settings.Contains('Title="DemiMedia Settings"')) 'Settings window must use the DemiMedia public product name.'
Assert-True ($settings.Contains('runtime that DemiMedia installs and verifies')) 'User-facing native-runtime copy must use DemiMedia.'

Write-Host "PASS: $assertions public identity assertions"
