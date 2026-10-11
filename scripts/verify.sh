#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet restore Provena.sln
dotnet build Provena.sln --no-restore --configuration Release
dotnet test tests/Provena.Api.UnitTests --no-build --configuration Release
dotnet test tests/Provena.Api.IntegrationTests --no-build --configuration Release
