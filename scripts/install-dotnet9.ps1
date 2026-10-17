param(
    [ValidatePattern('^9\.0\.[0-9]+$')]
    [string]$SdkVersion = '9.0.319',
    [string]$LogPath = (Join-Path $env:TEMP 'miniharness-dotnet9-install.log')
)
$ErrorActionPreference = 'Stop'
$installDirectory = 'C:\Program Files\dotnet'
$dotnet = Join-Path $installDirectory 'dotnet.exe'
$installer = Join-Path $env:TEMP ('miniharness-dotnet9-' + [Guid]::NewGuid().ToString('N') + '.exe')
Start-Transcript -Path ($LogPath + '.transcript.txt') -Force | Out-Null
try {
    if (Test-Path -LiteralPath $dotnet) {
        $installedSdks = & $dotnet --list-sdks
        if ($LASTEXITCODE -eq 0 -and ($installedSdks -match ('^' + [regex]::Escape($SdkVersion) + ' '))) {
            $installedSdks | Write-Output
            return
        }
    }
    # Download as the current user, then elevate only the verified offline installer.
    $releases = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/9.0/releases.json' -TimeoutSec 60
    $sdk = $releases.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $SdkVersion | Select-Object -First 1
    $package = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
    if ($null -eq $package) { throw 'Requested x64 SDK installer not found in Microsoft release metadata.' }
    Write-Output ('Downloading .NET SDK ' + $SdkVersion + ' from Microsoft.')
    Invoke-WebRequest -Uri $package.url -OutFile $installer -UseBasicParsing -TimeoutSec 300
    $actualHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA512).Hash
    if ($actualHash -ne $package.hash) { throw 'SDK installer SHA512 verification failed.' }
    Write-Output 'SHA512 verified. Installing alongside existing system SDKs; Windows may request administrator authorization.'
    $installation = Start-Process -FilePath $installer -Verb RunAs -WindowStyle Hidden -ArgumentList @('/install', '/quiet', '/norestart', '/log', ('"' + $LogPath + '"')) -Wait -PassThru
    if ($installation.ExitCode -ne 0 -and $installation.ExitCode -ne 3010) { throw ('SDK installer exit code: ' + $installation.ExitCode) }
    $installedSdks = & $dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw 'SDK verification failed.' }
    $installedSdks | Write-Output
    if (-not ($installedSdks -match ('^' + [regex]::Escape($SdkVersion) + ' '))) { throw 'The requested SDK was not installed.' }
    if ($installation.ExitCode -eq 3010) { Write-Output 'Windows reports that a restart is required to finish installation.' }
}
finally {
    if (Test-Path -LiteralPath $installer) { Remove-Item -LiteralPath $installer -Force }
    Stop-Transcript | Out-Null
}
