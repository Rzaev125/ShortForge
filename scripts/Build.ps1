param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $root 'artifacts/dotnet-home'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location (Join-Path $root 'src/ShortForge.Ui')
try {
    npm.cmd ci --no-audit --no-fund
    if ($LASTEXITCODE) { throw 'npm ci failed' }
    npm.cmd run build
    if ($LASTEXITCODE) { throw 'UI build failed' }
} finally { Pop-Location }
Push-Location $root
try {
    dotnet restore ShortForge.slnx --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    dotnet build ShortForge.slnx --no-restore -c $Configuration
    if ($LASTEXITCODE) { throw 'Solution build failed' }
} finally { Pop-Location }
