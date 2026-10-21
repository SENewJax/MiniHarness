param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory,
    [switch]$RequireSymlinkTests,
    [ValidateRange(0, 3)][int]$RaceRuns = 2,
    [switch]$LiveModel,
    [ValidateSet('Responses', 'ChatCompletions', 'Both')][string]$Protocol = 'Both'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$oldRoot = $env:MINIHARNESS_VALIDATION_ROOT
$oldStrict = $env:MINIHARNESS_REQUIRE_SYMLINK_TEST
$passed = $false
$transcribing = $false
Push-Location -LiteralPath $workspace
try {
    $base = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } elseif ($oldRoot) { [IO.Path]::GetFullPath($oldRoot) } else { Join-Path ([IO.Path]::GetTempPath()) 'MiniHarness/validation' }
    $runDirectory = Join-Path $base ('v3-acceptance-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $env:MINIHARNESS_VALIDATION_ROOT = $runDirectory
    if ($RequireSymlinkTests) { $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = '1' }
    Start-Transcript -LiteralPath (Join-Path $runDirectory 'acceptance.log') | Out-Null
    $transcribing = $true
    Write-Output "V3 evidence: $runDirectory"
    & "$PSScriptRoot/verify.ps1" -DotnetPath $DotnetPath -Stage all
    & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --coding-fixture
    if ($LASTEXITCODE -ne 0) { throw 'V1 compatibility fixture failed.' }
    for ($run = 1; $run -le $RaceRuns; $run++) {
        foreach ($stage in @('v3-compact', 'v3-budget', 'v3-recovery', 'v3-consistency')) {
            Write-Output "V3 control replay $run/$RaceRuns : $stage"
            & $DotnetPath run --project tests/MiniHarness.Tests -c Release --no-build -- --stage $stage
            if ($LASTEXITCODE -ne 0) { throw "V3 replay failed: $stage" }
        }
    }
    if ($LiveModel) {
        $protocols = if ($Protocol -eq 'Both') { @('Responses', 'ChatCompletions') } else { @($Protocol) }
        foreach ($upstream in $protocols) {
            & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --context-recovery $upstream
            if ($LASTEXITCODE -ne 0) { throw "Real V3 $upstream acceptance blocked/failed; do not automatically retry paid calls." }
        }
    } else { Write-Output 'No real models requested. Offline results do not replace actual summary/restart/steering evidence.' }
    $passed = $true
}
finally {
    if ($runDirectory) {
        $sources = @(& rg --files src tests scripts -g '*.cs' -g '*.csproj' -g '*.ps1' | Sort-Object | ForEach-Object {
            [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
        })
        [ordered]@{
            checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            sourceRevision = (& git rev-parse HEAD)
            sourceChanges = @(& git status --short)
            sources = $sources
            deterministicAndRequestedChecksPassed = $passed
            strictSymlinksRequired = [bool]$RequireSymlinkTests
            realModelRequested = [bool]$LiveModel
            protocol = $Protocol
            qualification = 'Inspect skips/failures, invocation purposes, source hashes and real host phase records. Only requested protocols are covered.'
            credentials = 'Process environment bundle; no credentials recorded.'
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Encoding utf8
    }
    if ($transcribing) { Stop-Transcript | Out-Null }
    $env:MINIHARNESS_VALIDATION_ROOT = $oldRoot
    $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = $oldStrict
    Pop-Location
}
