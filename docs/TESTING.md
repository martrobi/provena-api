# Verification and test strategy

## Prerequisites

Use .NET SDK 10 allowed by `global.json` (10.0.100, latestFeature roll-forward) and package-feed access/cache. Restore, Release build and unit tests do not require Docker. The full gate also requires a running Docker daemon able to start postgres:17. PowerShell is needed only for `verify.ps1`. Integration tests use their own disposable Testcontainer and temporary storage, not docker-compose's database. No local production credentials should be necessary for those tests.

## Local verification without Docker

From the repository root, run these commands in order:

```sh
dotnet restore Provena.sln
dotnet build Provena.sln --no-restore --configuration Release
dotnet test tests/Provena.Api.UnitTests/Provena.Api.UnitTests.csproj --no-build --configuration Release
```

This checks package restore, compilation with warnings-as-errors, and the unit test project. It does not run integration tests and is not the full verification gate. The unit test command uses the Release assembly produced by the preceding build.

## Full verification and focused commands

```sh
dotnet restore Provena.sln
dotnet build Provena.sln --no-restore --configuration Release
dotnet test tests/Provena.Api.UnitTests/Provena.Api.UnitTests.csproj --no-build --configuration Release
dotnet test tests/Provena.Api.IntegrationTests/Provena.Api.IntegrationTests.csproj --no-build --configuration Release
```

The full gate requires Docker. The normal full gate is `pwsh -File scripts/verify.ps1` or `bash scripts/verify.sh`; it performs restore, Release build, unit tests and integration tests. PowerShell checks for the SDK command, fails immediately on native-command errors, and restores its working directory even on failure. No skip-success path is provided. Docker is exercised by the integration fixture; failure there is not automatically a Docker problem—read the test output. No format/lint command is configured; warnings are errors during build.

For a focused feedback loop after a successful Release build, use `dotnet test tests/Provena.Api.UnitTests/Provena.Api.UnitTests.csproj --no-build --configuration Release --filter FullyQualifiedName~Status_rules_follow_documented_priority`. Do not report a filtered run as the full gate. No-build uses the last compiled assembly, so rebuild after code changes.

Optional live smoke: `python scripts/smoke.py --base-url http://localhost:5000`. Requires Python 3.10+ and an already configured/migrated running API. It creates persistent demo records in a new organization and writes an export ZIP; use only a disposable development environment. It is not part of the default gate and is not a load/security test.

## Actual tests (source inventory)

Unit `DomainTests.cs` has 3 Fact methods and 4 Theory methods with 18 InlineData rows: 21 cases by static count, not an executed/discovered test count. It covers token shape/hash/randomness, preservation of terminal states, state priority, CN format/leading zeroes, allowed/invalid geometry and file MIME/size.

`EndpointInventoryTests.cs` (3 Facts) reads `EndpointDataSource` and checks:
- the exact route/method set;
- authenticated vs anonymous access per endpoint;
- rate-limit policy names;
- 21 MiB request/multipart metadata on upload groups;
- that no endpoint requires antiforgery and no MVC controller types or controller endpoints exist.

`HttpContractTests.cs` (9 Facts) pins the HTTP contract that the Minimal API migration had to preserve. It was written and run green against the MVC controllers first. It covers:
- Location on create (absolute for catalog, absent for shipments);
- 204 for empty geolocation;
- optional complete body;
- case-insensitive/numeric query and form enum binding;
- 400/404/405/415 binding and routing failures;
- JSON depth and integer-enum rejection;
- the ProblemDetails envelope (traceId, no instance);
- 401 on every internal feature group, and cross-tenant 404 for file routes;
- download/export headers;
- CSV import happy paths with partial failure;
- the OpenAPI path set.

It registers only two tenants because the auth limiter is shared across the fixture.

`WorkflowTests.cs` has 2 Facts:

- `Full_http_flow_and_organization_isolation`: registration/login/Me, unauthenticated supplier denial, shipment/checklist, another tenant's supplier/case/export/shipment denial and foreign relationship rejection, hash storage, public projection, incomplete completion conflict, details/files/geometry, rejection/resubmission, internal review, ZIP entries, completion and terminal public-write rejection.
- `Token_rotation_revocation_and_expiry_are_enforced`: old-token denial after rotation, revoke, regenerated token and forced expiry.

`ApiFactory` starts PostgreSQL 17, configures Testing with generated JWT secret and temp storage, asserts no pending model changes, then MigrateAsync. Tests share a fixture and a nonparallel collection. Auth rate limits remain enabled; adding many registrations/logins in the same fixture/IP window can hit 429. Address test isolation deliberately rather than disabling production limits casually.

## What to test for a change

| Change | Meaningful checks |
| --- | --- |
| Pure rule / validator | Focused unit boundary and invalid-input cases; status precedence and terminal preservation where relevant |
| Endpoint / DTO / enum / status code | HTTP request/response assertions; validation and compatibility; relevant smoke contract; update the expected surface in `EndpointInventoryTests` for added/removed/re-protected routes |
| Tenant query / relation / download | Real PostgreSQL HTTP tests for organization A vs B, wrong-case resource and unauthenticated callers |
| EF schema | Clean migration, snapshot check, prior-schema upgrade with data, constraints and tenant-consistent relationships; `MigrationTests` covers clean creation and upgrade from the initial schema, so extend its upgrade test with rows for the table a new migration changes |
| Persistence guard | Empty/foreign tenant, attempted tenant reassignment, audit update/delete, timestamp scope and alternative write APIs |
| Supplier token / concurrency | Lifecycle negatives plus concurrent revoke/rotate waiting on case lock; validate no stale token succeeds |
| Evidence mutation / review | Accepted → changed evidence → Submitted; NotRequired behaviour; completion and terminal cases |
| Imports | Header/size/row limits, partial success, duplicate and missing references, cross-tenant references, multiline rows |
| Storage / export | Invalid files, cleanup failures, replaced geo, malicious filenames/CSV values, status-specific export contract |
| Retained evidence | `RetainedEvidenceTests` (document withdraw, geometry supersede incl. public portal and export, repeated details saves, resubmission after acceptance, persistence guard, bytes still readable through `IFileStorage`) and the retained-evidence cases in `MigrationTests` (partial indexes, upgrade with rows, Down guard). Never assert that committed evidence bytes are deleted |
| Documentation-only | Existing-path and link checks, claims against source, no accidental code diff |

Known missing coverage includes import header/size/row-limit and cross-tenant reference cases (only happy paths, partial failure and wrong file type are covered), Kestrel request-size enforcement (metadata only), archive/catalog-delete/edit restrictions, broad cross-tenant write/file/product paths, waiver permissions, rate limits/lockout, concurrent requests, storage failures, data-bearing upgrades beyond the initial schema and exact export-manifest/error contracts. There are also no focused tests for all SaveChanges guard boundaries. The two broad workflows are useful, not a complete regression suite. Do not add trivial tests that merely mirror documentation; add tests resolving the changed behaviour or risk.

## Reporting

Record exact commands and exit status; distinguish passed, failed, blocked and not run. Missing SDK/Docker/feed access is an environment blocker. Missing source is a repository blocker. An assertion failure is different from either. Never replace PostgreSQL integration tests with in-memory EF, remove HasPendingModelChanges, weaken warnings or skip failed tests just to report success. Update docs/known blockers when resolved and preserve the historical discovery result.
