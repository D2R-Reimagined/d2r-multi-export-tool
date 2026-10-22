# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
    Level C validation: one retained library export, independent parity, desktop root transitions.
.DESCRIPTION
    Uses a new session directory; never cleans existing output. Add -CheckCli
    for releases or CLI changes to repeat the export through the CLI.
    See docs/TESTING.md for selection rules and report review requirements.
#>
[CmdletBinding()]
param(
    [string] $ModData = $env:D2R_MOD_DATA,
    [string] $CascData = $env:D2R_CASC_DATA,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $SessionPath = (Join-Path ([IO.Path]::GetTempPath()) ('d2r-validation-' + [guid]::NewGuid().ToString('N'))),
    [switch] $CheckCli
)

$ErrorActionPreference = 'Stop'
foreach ($source in @($ModData, $CascData)) {
    if ([string]::IsNullOrWhiteSpace($source) -or !(Test-Path -LiteralPath $source -PathType Container)) {
        throw 'Required real-data validation could not execute: supply existing ModData and CascData roots.'
    }
}
if (Test-Path -LiteralPath $SessionPath) {
    throw 'SessionPath must be new; existing validation/output directories are never reused across sessions.'
}

$SessionPath = [IO.Path]::GetFullPath($SessionPath)
$ModData = (Resolve-Path -LiteralPath $ModData).Path
$CascData = (Resolve-Path -LiteralPath $CascData).Path
$bundle = Join-Path $SessionPath 'export'
$results = [Collections.Generic.List[object]]::new()
$savedEnvironment = @{}
foreach ($name in @('D2R_MOD_DATA', 'D2R_CASC_DATA', 'D2R_REQUIRE_REAL_DATA', 'D2R_VALIDATION_EXPORT')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-Check([string] $Name, [scriptblock] $Action) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "$Name failed (exit $LASTEXITCODE)." }
        $results.Add([pscustomobject]@{ Check = $Name; Status = 'Passed'; Seconds = $timer.Elapsed.TotalSeconds })
    }
    catch {
        $results.Add([pscustomobject]@{ Check = $Name; Status = 'Failed'; Seconds = $timer.Elapsed.TotalSeconds; Message = $_.Exception.Message })
        throw
    }
}

function Assert-TestResults([string] $Path) {
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int] $counts.total -eq 0 -or [int] $counts.passed -ne [int] $counts.total) {
        throw "Required suite did not fully pass: total=$($counts.total), passed=$($counts.passed), failed=$($counts.failed), notExecuted=$($counts.notExecuted)."
    }
}

function Read-CompleteManifest {
    $manifest = Get-Content -LiteralPath (Join-Path $bundle 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 2 -or !$manifest.Succeeded -or !$manifest.Complete -or
        @($manifest.Files).Count -eq 0 -or @($manifest.Steps).Count -eq 0 -or
        @($manifest.Steps | Where-Object { $_.Status -ne 'Written' -or ($null -ne $_.NotWritten -and @($_.NotWritten).Count -ne 0) }).Count -ne 0) {
        throw 'Required export is failed, incomplete, or has missing declared outputs.'
    }
    return $manifest
}

try {
    Push-Location (Split-Path -Parent $PSScriptRoot)
    New-Item -ItemType Directory -Path $SessionPath | Out-Null
    $env:D2R_MOD_DATA = $ModData
    $env:D2R_CASC_DATA = $CascData
    $env:D2R_REQUIRE_REAL_DATA = '1'
    $env:D2R_VALIDATION_EXPORT = $bundle

    Invoke-Check 'Build' { dotnet build .\D2RMultiExport.sln -c $Configuration }
    Invoke-Check 'Library and intermediate real-data assertions' {
        dotnet test .\D2RMultiExport.Tests -c $Configuration --no-build --logger 'trx;LogFileName=library.trx' --results-directory $SessionPath
        if ($LASTEXITCODE -eq 0) { Assert-TestResults (Join-Path $SessionPath 'library.trx') }
    }
    Invoke-Check 'Manifest success and completeness' { $script:firstManifest = Read-CompleteManifest }
    Invoke-Check 'Python auditor regressions' { python -B -m unittest discover -s tools -p test_property_range_audit.py -v }
    Invoke-Check 'Independent source parity and payload integrity' {
        python -B tools/property_range_audit.py --export $bundle > (Join-Path $SessionPath 'property-audit.json')
    }
    Invoke-Check 'Desktop interactions, shared parity, root removal/restoration' {
        dotnet test .\D2RMultiExport.Client.Tests -c $Configuration --no-build --logger 'trx;LogFileName=desktop.trx' --results-directory $SessionPath
        if ($LASTEXITCODE -eq 0) { Assert-TestResults (Join-Path $SessionPath 'desktop.trx') }
    }

    if ($CheckCli) {
        $sentinel = Join-Path $bundle 'validation-sentinel.txt'
        Set-Content -LiteralPath $sentinel -Value 'unrelated validation sentinel'
        Invoke-Check 'Independent CLI repeat, determinism and stale-file preservation' {
            dotnet run --project .\D2RMultiExport.Console -c $Configuration --no-build -- export --mod-data $ModData --casc-data $CascData --out $bundle --early-stop false
            if ($LASTEXITCODE -ne 0) { throw "CLI export failed (exit $LASTEXITCODE)." }
            $secondManifest = Read-CompleteManifest
            $firstFiles = @($firstManifest.Files | Sort-Object Path | Select-Object Path, Sha256, Bytes | ConvertTo-Json -Depth 5)
            $secondFiles = @($secondManifest.Files | Sort-Object Path | Select-Object Path, Sha256, Bytes | ConvertTo-Json -Depth 5)
            if (Compare-Object $firstFiles $secondFiles) { throw 'Repeated export changed manifest payload entries.' }
            if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -ne 'unrelated validation sentinel') { throw 'CLI modified the unrelated sentinel.' }
        }
    }

    Write-Host "Inspect current-run reports and affected JSON in $bundle before submission (docs/TESTING.md §2.1)."
    Get-Content -LiteralPath (Join-Path $bundle 'extras/import-report.txt') -TotalCount 12
}
finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    Pop-Location
    if (Test-Path -LiteralPath $SessionPath -PathType Container) {
        $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $SessionPath 'validation-results.json')
        $results | Format-Table -AutoSize
        Write-Host "Validation session retained at $SessionPath"
    }
}
