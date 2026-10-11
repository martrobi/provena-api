# Provena backend

.NET 10 ASP.NET Core Web API for shipment evidence collection, supplier requests, review and operational exports.

This revision includes `Data/AppDbContext.cs`, the design-time factory, initial migration and model snapshot. The earlier ZIP omitted these because the source directory was ignored; that archive issue is resolved. Source review does not establish a passing build or test run. See [discovery findings](docs/AI_DISCOVERY_REPORT.md).

- AI coding entry (Codex + Claude Code): [AGENTS.md](AGENTS.md)
- Owner's practical guide (Croatian): [AI_DEVELOPMENT_GUIDE_HR.md](docs/AI_DEVELOPMENT_GUIDE_HR.md)
- Code locations: [CODEBASE_MAP.md](docs/CODEBASE_MAP.md)
- Local configuration and launch: [DEPLOYMENT.md](docs/DEPLOYMENT.md)
- Build/test prerequisites and commands: [TESTING.md](docs/TESTING.md)

With .NET 10 + Docker available, use `pwsh -File scripts/verify.ps1` or `bash scripts/verify.sh`. Neither script deploys or migrates a production database. Integration tests create a disposable PostgreSQL container. No successful build/test run is claimed for this archive.
