param([string]$DotnetPath = 'dotnet', [ValidateSet('all','legacy','1','2','3','4-5','6','7','v0-control','v0-replan','v0-resource','v0-child','v0-storage','v1-tool','v1-workspace','v1-mutation','v1-ledger','v1-process','v1-verify','v1-policy','v1-e2e','consistency','v2-session','v2-output','v2-resource','v2-wait','v2-steer','v2-recovery','v2-consistency','v3-context','v3-compact','v3-budget','v3-recovery','v3-migration','v3-consistency','v4-acceptance','v4-evidence','v4-artifact','v4-progress','v4-delivery','v4-recovery','v4-consistency')][string]$Stage = 'all')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $workspace
try {
    & $DotnetPath build MiniHarness.sln --configuration Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. A .NET 9 SDK is required; a runtime alone cannot compile this project.' }
    if ($Stage -eq '7') {
        & $DotnetPath run --project tests/MiniHarness.LiveTests --configuration Release --no-build -- --runtime
    }
    else {
        & $DotnetPath run --project tests/MiniHarness.Tests --configuration Release --no-build -- --stage $Stage
    }
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
}
finally { Pop-Location }
