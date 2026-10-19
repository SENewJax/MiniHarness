param(
    [string]$DotnetPath = 'dotnet',
    [string]$OutputDirectory,
    [switch]$RequireSymlinkTests,
    [ValidateRange(0, 3)][int]$RaceRuns = 2,
    [switch]$LiveModel,
    [ValidateSet('Responses', 'ChatCompletions')][string]$Protocol = 'ChatCompletions'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$oldRoot = $env:MINIHARNESS_VALIDATION_ROOT
$oldStrict = $env:MINIHARNESS_REQUIRE_SYMLINK_TEST
$transcribing = $false
$passed = $false
Push-Location -LiteralPath $workspace
try {
    $base = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } elseif ($oldRoot) { [IO.Path]::GetFullPath($oldRoot) } else { Join-Path ([IO.Path]::GetTempPath()) 'MiniHarness/validation' }
    $runDirectory = Join-Path $base ('v2-acceptance-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    $env:MINIHARNESS_VALIDATION_ROOT = $runDirectory
    if ($RequireSymlinkTests) { $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = '1' }
    Start-Transcript -LiteralPath (Join-Path $runDirectory 'acceptance.log') | Out-Null
    $transcribing = $true
    Write-Output "V2 evidence: $runDirectory"
    if (-not [OperatingSystem]::IsWindows()) { throw 'V2 owned process platform unavailable; acceptance is blocked.' }
    & "$PSScriptRoot/verify.ps1" -DotnetPath $DotnetPath -Stage all
    & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --coding-fixture
    if ($LASTEXITCODE -ne 0) { throw 'V1 coding fixture compatibility failed.' }
    for ($run = 1; $run -le $RaceRuns; $run++) {
        foreach ($stage in @('v2-wait', 'v2-steer', 'v2-recovery', 'v2-consistency')) {
            Write-Output "V2 control replay $run/$RaceRuns : $stage"
            & $DotnetPath run --project tests/MiniHarness.Tests -c Release --no-build -- --stage $stage
            if ($LASTEXITCODE -ne 0) { throw "V2 control replay failed: $stage" }
        }
    }
    if ($LiveModel) {
        & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --long-running $Protocol
        if ($LASTEXITCODE -ne 0) { throw 'Real-model V2 acceptance blocked or failed; retain the current report.' }
    } else { Write-Output 'Real-model V2 acceptance was not run; deterministic success does not replace it.' }
    $passed = $true
}
finally {
    if ($runDirectory) {
        $manifest = [ordered]@{
            checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
            sourceRevision = (& git rev-parse HEAD)
            sourceChanges = @(& git status --short)
            diffSummary = @(& git diff --stat)
            deterministicAndRequestedChecksPassed = $passed
            strictSymlinksRequired = [bool]$RequireSymlinkTests
            realModelRequested = [bool]$LiveModel
            protocol = $Protocol
            credentials = 'Environment bundle only; no credentials recorded.'
            platform = [Environment]::OSVersion.VersionString
            qualification = 'Inspect skips and failures. Only requested live protocol is covered; no other protocol implied.'
        }
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDirectory 'manifest.json') -Encoding utf8
    }
    if ($transcribing) { Stop-Transcript | Out-Null }
    $env:MINIHARNESS_VALIDATION_ROOT = $oldRoot
    $env:MINIHARNESS_REQUIRE_SYMLINK_TEST = $oldStrict
    Pop-Location
}
