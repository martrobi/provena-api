# Provena agent entry point

Provena is a .NET 10 controller-based API for collecting and reviewing EUDR workflow evidence. It does not determine legal compliance or submit a DDS.

Read this file first; **do not load all documentation or skills by default**. Choose the smallest relevant route below, then inspect the named implementation and its tests. Paths are relative to the repository root.

## Evidence and authority

For current behaviour, source code and executable tests are primary evidence; an unrun test is an expectation, not proof. Explicit owner-approved domain/security decisions govern intended changes. Documentation describes these; plans describe proposed changes and never silently override current behaviour. A task can explicitly request a change, but conflicts with an approved rule must be surfaced. Label observations, project rules, recommendations and unknowns separately. Do not normalize a security defect into a rule.

Persistence lives in `src/Provena.Api/Data/AppDbContext.cs` and `Data/Migrations/`. Read [DATABASE.md](docs/DATABASE.md) for tenant filters/write guards, constraints and the initial migration/snapshot coupling before schema work. Source review is not a passing runtime test.

## Task routing

| Change | Read only these initially | Procedure when needed |
| --- | --- | --- |
| Locate code / small local fix | [CODEBASE_MAP](docs/CODEBASE_MAP.md), affected source/test | — |
| Product scope | [PRODUCT](docs/PRODUCT.md) | — |
| Service boundaries / DI / transactions | [ARCHITECTURE](docs/ARCHITECTURE.md) | — |
| Entity / query / schema / migration | [DATABASE](docs/DATABASE.md) | [database-change](.agents/skills/database-change/SKILL.md) |
| Endpoint / DTO / CSV / export | [API](docs/API.md) | [api-contract-change](.agents/skills/api-contract-change/SKILL.md) |
| Case state / evidence / producer / plot | [EUDR_DOMAIN](docs/EUDR_DOMAIN.md) | [eudr-domain-change](.agents/skills/eudr-domain-change/SKILL.md) |
| Auth / tenant / public token / upload / logging | [SECURITY](docs/SECURITY.md) | [security-sensitive-change](.agents/skills/security-sensitive-change/SKILL.md) |
| Tests / verification | [TESTING](docs/TESTING.md) | [verify-change](.agents/skills/verify-change/SKILL.md) |
| Runtime / configuration / containers | [DEPLOYMENT](docs/DEPLOYMENT.md) | — |
| Significant cross-cutting change | Relevant rows above; [plan template](docs/plans/TEMPLATE.md) | Owner approves unresolved domain/architecture decisions |

Use [discovery findings](docs/AI_DISCOVERY_REPORT.md) only for baseline issues or technical-debt work. Human onboarding: [Croatian guide](docs/AI_DEVELOPMENT_GUIDE_HR.md). Important decisions: [ADR index](docs/adr/README.md).

## Project rules

- Follow existing controller → scoped service → DbContext boundaries. Do not add repositories, CQRS, mediator, new projects or packages without a concrete approved need. Match nearby style; avoid incidental reformatting.
- Keep validation server-side, preserve cancellation, transactions, per-case locks and post-lock token rechecks. Queries returning tenant data must be tenant-scoped; verify enforcement, including writes and foreign references. Never trust client `OrganizationId`.
- Keep internal routes authenticated; the public capability grants access to one request/case only. No new `IgnoreQueryFilters` use without explicit security review. Never expose token hashes, storage keys, internal notes or audit payloads through public DTOs.
- Never log passwords, bearer tokens, public URLs, raw evidence or connection secrets. Do not commit secrets or uploaded data. Frontend checks do not authorize backend operations.
- Supplier evidence is not reviewer acceptance. Automated geometry/format validation is not regulatory verification. `ReadyForExport` and `Completed` are operational states. Preserve current transition order unless explicitly changing it.
- Supplier, Producer and ProductionPlot are distinct target concepts, not interchangeable entities; only Supplier exists today. Ask the owner about new relationships, legal interpretations, review permissions, retention or changed status semantics before implementing those decisions. Continue unrelated unambiguous work.
- Do not weaken tests, auth, tenant boundaries, warning-as-error settings or validation to get a green build. Do not bypass persistence guards through other SaveChanges overloads, bulk writes or raw SQL without explicit review and equivalent enforcement.

## Finish the change

For code/tooling changes, run `pwsh -File scripts/verify.ps1` (or `bash scripts/verify.sh`) with .NET 10 and Docker available. See TESTING for focused checks. No skipped check counts as a pass; report commands, exit status, blockers and residual risk. Documentation-only changes need path/content/diff checks, not fabricated runtime evidence.

Update the owning document when public contracts, domain rules, security boundaries, persistence, configuration or verification commands change. Update the map when code moves. Add an ADR only for a durable significant decision. Trivial internal edits need no documentation ceremony. Final report: behaviour changed, files, evidence/tests, unresolved decisions. Never silently fix unrelated findings.
