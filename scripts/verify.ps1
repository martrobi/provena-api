[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'The dotnet SDK is required. See global.json and docs/TESTING.md.'
    }

    # Fail on each native-command exit code; never treat skipped checks as success.
    dotnet restore Provena.sln
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed. Read the output for SDK or package-feed errors.' }

    dotnet build Provena.sln --no-restore --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed. Read the compiler output; do not skip failed checks or weaken warnings.' }

    dotnet test tests/Provena.Api.UnitTests/Provena.Api.UnitTests.csproj --no-build --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }

    dotnet test tests/Provena.Api.IntegrationTests/Provena.Api.IntegrationTests.csproj --no-build --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed. Inspect the output; Docker/PostgreSQL startup and test assertions are separate failure causes.' }

    Write-Host 'PASS: restore, Release build, unit tests and integration tests.'
} finally {
    Pop-Location
}
