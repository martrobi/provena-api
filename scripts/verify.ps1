$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore Provena.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    dotnet build Provena.sln --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet test tests/Provena.Api.UnitTests --no-build --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    dotnet test tests/Provena.Api.IntegrationTests --no-build --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed. Verify that Docker is running.' }
} finally { Pop-Location }
