#requires -Version 5.1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$files = @(& git -C $root ls-files '*.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked PowerShell files.' }

$failures = [Collections.Generic.List[string]]::new()
foreach ($relative in $files) {
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $root $relative),
        [ref]$tokens,
        [ref]$errors
    )
    foreach ($errorRecord in @($errors)) {
        $failures.Add("${relative}:$($errorRecord.Extent.StartLineNumber): $($errorRecord.Message)")
    }
}

if ($failures.Count -ne 0) {
    throw "PowerShell parse failures:`n$($failures -join [Environment]::NewLine)"
}

Write-Host "PowerShell syntax: PASS ($($files.Count) tracked scripts, $($PSVersionTable.PSVersion))"
