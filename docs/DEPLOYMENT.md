# Runtime and deployment assumptions

## Supplied infrastructure

`docker-compose.yml` runs PostgreSQL 17 only, bound to 127.0.0.1:5432, with a named persistent volume and pg_isready health check. There is no API Dockerfile/container service, CI/CD workflow, cloud deployment manifest, health endpoint, reverse-proxy configuration or backup automation. No production platform is selected here.

The API expects .NET 10 and PostgreSQL via Npgsql. Uploaded files live on local disk. Multiple API replicas would need a reviewed shared-storage solution; singleton LocalFileStorage is per process, not distributed infrastructure. Package versions are listed in [CODEBASE_MAP](CODEBASE_MAP.md); successful restore has not been verified.

## Configuration ownership

| Key / environment form | Default or requirement |
| --- | --- |
| `ConnectionStrings:Default` / `ConnectionStrings__Default` | Required nonblank PostgreSQL connection string; blank in tracked appsettings |
| `Jwt:Secret` / `Jwt__Secret` | Required unique secret >=32 UTF-8 bytes; placeholder containing SET-USING rejected |
| `Jwt:Issuer`, `Jwt:Audience` | Provena.Api / Provena.Frontend |
| `Jwt:AccessTokenMinutes` | 60, startup accepts 1–1440 |
| `Frontend:BaseUrl` / `Frontend__BaseUrl` | http://localhost:5173, absolute http/https required; used for supplier links |
| `Cors:AllowedOrigins` / `Cors__AllowedOrigins__0` etc. | localhost 5173 and 4200; wildcard entry rejected |
| `FileStorage:RootPath` / `FileStorage__RootPath` | ./data/files resolved relative to ContentRootPath; writable persistent directory required |
| `FileStorage:MaxFileSizeMb` | 20, accepted 1–20 |
| `SupplierRequests:ExpiryDays` | 30, accepted 1–90 |
| `SeedDemoData`, `Seed:Password` | false; seeding runs only in Development with flag true and nonblank password |
| `AllowedHosts` | `*`; deployment should review host filtering |

ASP.NET configuration providers are used via CreateBuilder. `.env.example` supplies the compose password only; the API does not automatically import a dotenv file. Set its connection string separately. Use user-secrets or environment configuration for credentials, not tracked JSON. `appsettings.Development.json.example` is a template and is not loaded under that filename.

## Local launch

1. Use this revision with the tracked `src/Provena.Api/Data/` files. Read [DATABASE](DATABASE.md) before adding a migration; do not create a second InitialCreate migration.
2. Copy `.env.example` to `.env` and set a local PostgreSQL password. Run `docker compose up -d postgres`.
3. Configure ConnectionStrings:Default and Jwt:Secret securely for `src/Provena.Api` (its UserSecretsId is `provena-api-development`). Connection values must match compose. Keep demo seeding false initially.
4. Restore/build, restore EF tools and review/apply migrations to the local database using the commands and prerequisites in DATABASE. API startup does not create/migrate a schema. DesignTimeFactory needs the connection string but does not run Program startup validation or require a JWT secret for EF commands.
5. Run `dotnet run --project src/Provena.Api --launch-profile Provena.Api`. The supplied profile uses Development and http://localhost:5000; visit `/scalar` for local API exploration.
6. Optionally configure a fresh demo password and enable SeedDemoData. Seeder creates/reuses demo@provena.local, a sample organization, supplier/product and one shipment. It does not set up a schema or reset an existing user's password.

These are source-derived setup instructions, not a successful deployment transcript. Do not point migrations or smoke tests at production during local verification.

## Deferred production work (not the current development phase)

The owner currently develops locally and does not want CI/CD, deployment or hosting work. The following items remain future operational considerations, not prerequisites for ordinary local feature development. See [LOCAL_DEVELOPMENT](LOCAL_DEVELOPMENT.md).

- Real email delivery: DevelopmentEmailSender is registered in every environment and logs instead of sending. SentAtUtc is not proof of delivery. Delivery currently follows DB commit; decide retry/outbox and one-time link handling.
- TLS/proxy/forwarded-header trust, correct client IP for throttling, token-path redaction in access logs, allowed origins/hosts and safe supplier frontend handling.
- Durable protected file volume, quotas/scanning, coordinated database/file backups, restore drill, orphan cleanup and multi-instance consistency.
- Reviewed migration deployment/rollback strategy, observability/health, package restore reproducibility and CI gates. No automatic production migration step is introduced by this task.
- No external EU/DDS integration exists. A completed case's reference is user-entered; deployment cannot turn it into a verified submission.

Integration verification details belong to [TESTING](TESTING.md); security constraints to [SECURITY](SECURITY.md). Do not duplicate them in an infrastructure template that does not yet exist.
