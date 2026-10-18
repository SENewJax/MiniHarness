param(
    [string]$DotnetPath = 'dotnet',
    [switch]$RequireSymlinkTests,
    [ValidateRange(1, 10)][int]$HttpRuns = 3,
    [string]$OutputDirectory,
    [switch]$LiveModel,
    [ValidateSet('Responses', 'ChatCompletions')][string]$Protocol = 'ChatCompletions'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$previousRequirement = $env:MINIHARNESS_REQUIRE_SYMLINK_TEST
$previousValidationRoot = $env:MINIHARNESS_VALIDATION_ROOT
$transcribing = $false
Push-Location -LiteralPath $workspace
try {
    $outputBase = if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
        [IO.Path]::GetFullPath($OutputDirectory)
    } elseif (-not [string]::IsNullOrWhiteSpace($previousValidationRoot)) {
        [IO.Path]::GetFullPath($previousValidationRoot)
    } else {
        Join-Path ([IO.Path]::GetTempPath()) 'MiniHarness/validation'
    }
    $logDirectory = Join-Path $outputBase ('v1-acceptance-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $env:MINIHARNESS_VALIDATION_ROOT = $logDirectory
    $logPath = Join-Path $logDirectory 'acceptance.log'
    Start-Transcript -LiteralPath $logPath | Out-Null
    $transcribing = $true
    Write-Output "Acceptance log: $logPath"
    if ($RequireSymlinkTests) { $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = '1' }
    & "$PSScriptRoot/verify.ps1" -DotnetPath $DotnetPath -Stage all
    & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --coding-fixture
    if ($LASTEXITCODE -ne 0) { throw 'Offline coding acceptance driver failed.' }
    for ($run = 1; $run -le $HttpRuns; $run++) {
        Write-Output "HTTP continuation observation $run/$HttpRuns"
        & $DotnetPath run --project tests/MiniHarness.Tests -c Release --no-build -- --stage 6
        if ($LASTEXITCODE -ne 0) { throw "HTTP continuation failed; see $logDirectory/http-diagnostics." }
    }
    if ($LiveModel) {
        & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --coding $Protocol
        if ($LASTEXITCODE -ne 0) { throw 'Real-model coding acceptance is blocked or failed; inspect the retained report.' }
    }
    else { Write-Output 'Real-model acceptance was not requested; no claim of live completion is made.' }
    if (-not $RequireSymlinkTests) { Write-Output 'Symlink tests may be skipped. Use -RequireSymlinkTests in a permitted environment for final acceptance.' }
}
finally {
    if ($transcribing) { Stop-Transcript | Out-Null }
    $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = $previousRequirement
    $env:MINIHARNESS_VALIDATION_ROOT = $previousValidationRoot
    Pop-Location
}
