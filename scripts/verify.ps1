$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore Miau.slnx
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build Miau.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test Miau.slnx -c Release --no-build --logger trx
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }
