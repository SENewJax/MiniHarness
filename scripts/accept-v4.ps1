param([string]$DotnetPath = 'dotnet', [string]$OutputDirectory = '', [int]$RaceRuns = 2, [switch]$LiveModel, [switch]$NoBuild,
    [ValidateSet('Both','Responses','ChatCompletions')][string]$Protocol = 'Both')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$previousRoot = $env:MINIHARNESS_VALIDATION_ROOT
$success = $false
$sourcesBefore = @()
if ($OutputDirectory.Length -eq 0) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('MiniHarness/validation/v4-acceptance-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location -LiteralPath $workspace
$env:MINIHARNESS_VALIDATION_ROOT = $OutputDirectory
Start-Transcript -LiteralPath (Join-Path $OutputDirectory 'acceptance.log') | Out-Null
try {
    Write-Output "V4 evidence: $OutputDirectory"
    $sourcesBefore = @(& rg --files src tests scripts -g '*.cs' -g '*.csproj' -g '*.ps1' | Sort-Object | ForEach-Object { @{path=$_; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
    if (-not $NoBuild) {
        & $DotnetPath build MiniHarness.sln -c Release --nologo 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    }
    & $DotnetPath run --project tests/MiniHarness.Tests -c Release --no-build -- --stage all 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'all.log')
    if ($LASTEXITCODE -ne 0) { throw 'Full regression failed.' }
    & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --coding-fixture 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'coding.log')
    if ($LASTEXITCODE -ne 0) { throw 'V1 compatibility fixture failed.' }
    for ($iteration = 0; $iteration -lt $RaceRuns; $iteration++) {
        foreach ($stage in @('v4-acceptance','v4-artifact','v4-evidence','v4-progress','v4-delivery','v4-recovery','v4-consistency')) {
            & $DotnetPath run --project tests/MiniHarness.Tests -c Release --no-build -- --stage $stage 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory ("replay-$iteration-$stage.log"))
            if ($LASTEXITCODE -ne 0) { throw "V4 replay failed: $stage" }
        }
    }
    if ($LiveModel) {
        $protocols = if ($Protocol -eq 'Both') { @('Responses','ChatCompletions') } else { @($Protocol) }
        foreach ($upstream in $protocols) {
            & $DotnetPath run --project tests/MiniHarness.LiveTests -c Release --no-build -- --delivery $upstream 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory ("live-$upstream.log"))
            if ($LASTEXITCODE -ne 0) { throw "Real V4 $upstream blocked/failed; no automatic paid retry." }
        }
    }
    $success = $true
}
finally {
    $sources = @(& rg --files src tests scripts -g '*.cs' -g '*.csproj' -g '*.ps1' | Sort-Object | ForEach-Object { @{path=$_; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash} })
    $mismatches = @($sourcesBefore | Where-Object { $oldSource = $_; -not ($sources | Where-Object { $_.path -eq $oldSource.path -and $_.sha256 -eq $oldSource.sha256 }) })
    if ($mismatches.Count -gt 0 -or $sources.Count -ne $sourcesBefore.Count) { $success = $false }
    @{ checkedAtUtc=[DateTimeOffset]::UtcNow.ToString('o'); passed=$success; realModelRequested=[bool]$LiveModel; sources=$sources;
       sourceRevision=(& git rev-parse HEAD); sourceMismatches=$mismatches.Count; buildRequested=(-not [bool]$NoBuild);
       qualification='NoBuild requires an explicitly verified prior build. Inspect skips and live reports separately. No credentials recorded.' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'manifest.json') -Encoding utf8
    Stop-Transcript | Out-Null
    $env:MINIHARNESS_VALIDATION_ROOT = $previousRoot
    Pop-Location
}
if (-not $success) { throw 'Acceptance incomplete or sources changed during validation.' }
