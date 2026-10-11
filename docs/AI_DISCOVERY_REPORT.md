# AI foundation discovery report

> Historical discovery snapshot. Current coding-agent instructions live in `AGENTS.md`; current practical setup for Codex and Claude Code lives in `docs/AI_DEVELOPMENT_GUIDE_HR.md`. Do not treat this report as active agent configuration.

Initial review: 2026-10-11; persistence revision: 2026-10-12 (Europe/Zagreb). Current source: `provena-api-main (2).zip`, archive comment `50202c80d9b9fac78886219ee2d01d0fb7147305`. The initial archive was `fb87c157ace5fb6ab29cd343194776007703d8d2`. No live Git repository, database or frontend was accessed.

## Executive finding and historical correction

**The prior missing-persistence finding is resolved.** The second archive adds AppDbContext, DesignTimeFactory, InitialCreate and AppDbContextModelSnapshot, and narrows `.gitignore` from `**/data/` to `**/data/files/`. All other original file bytes match the first archive. The source directory had been ignored; no replacement persistence implementation is needed.

Source review now confirms ten tenant query filters, a tracked async-write ownership guard, composite tenant-aware foreign keys, unique indexes and application-level audit append-only checks. These are concrete controls, not assumptions. Build, migrations and tests still require execution in an environment with the necessary tools; no successful runtime result is claimed here.

## Discovery model and architecture

All original source, tests, configuration and scripts were inspected in the first review. This revision inspected the four added files and compared the archives rather than repeating unrelated discovery.

The application is one .NET 10 ASP.NET Core API: controller → service → EF/Identity DbContext, with PostgreSQL, local files, CSV import, ZIP export and JWT/internal versus capability/public access. No repository layer, mediator, external DDS integration or background worker is supplied. DevelopmentEmailSender is a logging stub in every environment.

Organization owns catalog and workflow records. A shipment has one product/supplier and a case with requirements, current submission/geolocation, documents, request history and audit. Main relationships have composite tenant FKs; some attribution IDs have none. Case mutations commonly transact and acquire a PostgreSQL row lock; public calls revalidate their capability after locking. File cleanup and post-commit email are not atomic with the database. Status calculation distinguishes evidence submission from internal review. Supplier, Producer and ProductionPlot remain separate target concepts; only Supplier is implemented.

## Strengths grounded in source

- Small navigable service architecture, explicit contracts/validators and central case status calculation.
- Random supplier tokens, hash-only intended persistence, expiry/revocation checks and post-lock revalidation.
- Query filters on all ten tenant types; tracked-write tenant checks; major foreign references protected by organization-inclusive keys.
- Per-organization external/shipment reference uniqueness, token uniqueness, one membership per user and bounded case/submission/geometry cardinalities.
- Audit update/delete rejection on the normal tracked async-save path and UTC timestamp updates for modified Entity rows.
- Evidence changes reset review metadata; file limits, generated storage keys, topology checks, ZIP sanitization and CSV formula-prefix handling.
- Real PostgreSQL Testcontainers tests with model-snapshot assertion, two HTTP flows and focused unit rules tests.

## Findings and recommended follow-up

Priorities express engineering order, not formal vulnerability scores. Findings were documented, not silently fixed.

| ID / priority | Evidence and implication | Follow-up |
| --- | --- | --- |
| F01 / resolved | The initial archive omitted Data source; the corrected archive supplies it. | Keep Data tracked. No need to regenerate the model or initial migration. |
| F02 / high | Authorize protects internal controllers, but no reviewer/Owner policies or fallback policy; live membership checked only by Me. | Decide backend permissions and session invalidation; add denial tests. |
| F03 / high before rollout | Email stub registered in every environment; SentAtUtc set without actual delivery; regenerate only returns a link. | Implement approved delivery/retry semantics separately. |
| F04 / high for historical evidence | Completed cases refer to mutable supplier/product rows; geometry is overwritten in place. | Decide snapshots, revision history and retention. |
| F05 / medium | Upload validation trusts client MIME/extension; no malware scan/quota/durable cleanup; geometry parsed before token resolution. | Review scanning, resource limits and file recovery for public exposure. |
| F06 / medium | No proxy/IP-trust/deployment configuration; URL tokens may appear in external logs; rate limits are process-local. | Review hosting/TLS/redaction/client IP and scaling. |
| F07 / medium | Producer/ProductionPlot identities and relations are absent. | Plan target model per ADR 0002; do not infer identity from polygons. |
| F08 / medium | Readiness does not require supplier completion; NotRequired needs no reason; any valid details save marks SupplierInformation submitted. | Confirm business rules and test approved changes. |
| F09 / medium | Export works in all statuses and writes audit on GET; completion accepts optional unverified DDS reference. | Confirm operational semantics; no compliance or DDS-submission claim. |
| F10 / medium | Imports partially commit and accept defined numeric enum strings; reference uniqueness is now confirmed in model/SQL. | Add import/duplicate/tenant tests; decide idempotency separately. |
| F11 / medium | Missing broad tenant write/file, concurrency, failure-path and schema-upgrade coverage. | Extend tests around changed boundaries; existing flows are not exhaustive. |
| F12 / low | Unused-in-source SSH.NET integration-test reference; no lock file or CI evidence. | Verify restore; review dependency/reproducibility separately. |
| F13 / addressed by foundation | Empty original README and no contextual architecture/domain/security docs. | Maintain authoritative routed docs; avoid bulk formatting. |
| F14 / high before next migration | InitialCreate.BuildTargetModel calls AppDbContextModelSnapshot.BuildInitialModel; the snapshot is the file EF regenerates. Helper removal can break compilation, or mutation can change historical target metadata. | Freeze the original target model independently before adding a migration; preserve initial schema/history. See DATABASE. |
| F15 / medium | Ownership/audit/timestamp checks exist only in SaveChangesAsync(CancellationToken). Other save overloads, raw/bulk writes are outside it; no RLS/audit trigger. | Preserve guarded writes; test guard scope and require equivalent enforcement for alternate APIs. |
| F16 / medium | SupplierRequestId on document/geo/audit and actor/reviewer IDs lack FKs. Request supplier equality with shipment supplier is not a DB invariant. | Preserve service associations; add constraints only with reviewed need and migration/tests. |
| F17 / low | Timestamp update loop excludes ApplicationUser/OrganizationUser; business rows have no row-version token. | Document scope; change only for a concrete requirement. |

## Contradictions and limits

- The earlier incomplete-archive diagnosis no longer applies. Active instructions now describe actual persistence instead of requiring recovery.
- Filters and constraints exist in source, but static review is not proof that a live database has those constraints or that tests pass.
- The snapshot comment calls initial mapping immutable, yet the initial migration depends on a class intended to evolve. A comment does not isolate historical metadata.
- Append-only audit is enforced through one application save path, not as tamper-proof database storage. Case export locking does not serialize independent catalog updates.
- Terminal states are preserved by Calculate, but Archive can move Completed to Archived, while revoke/export remain possible. Completed-case global immutability is not implemented.
- GeoValidationStatus.Invalid exists, but invalid geometry is rejected rather than persisted by the upload path.
- Initial migration uses hand-authored raw SQL; HasPendingModelChanges checks model vs snapshot, not full SQL/schema equivalence. Clean migrations and real schema tests remain necessary. Initial Down uses DROP TABLE ... CASCADE and loses data.
- No external architecture specification was used to invent a missing Clean Architecture layer or declare the current service structure wrong.

## Deliberately unresolved decisions

Supplier–Producer cardinality; plot identity/ownership and shipment attribution; waiver/reviewer permissions; membership revocation; retention/version history and catalog snapshots; supplier completion as readiness prerequisite; incomplete/archived exports; delivery retries; production hosting and regulatory interpretation. The existing behaviour remains unchanged. Raise focused questions with the relevant feature, not one global approval checklist.

## Foundation delivered and maintenance

The requested structure is retained: concise AGENTS router, ten topical/owner documents plus this report, five repository skills, ADR/plan templates and two evidence-labeled ADRs. README points to the canonical `AGENTS.md` entry point without duplicating the rule set. No new product feature, application abstraction, dependency, CI deployment or infrastructure was introduced.

This revision replaces obsolete missing-source guidance in every affected document/procedure with observed persistence behaviour, scope limits and migration precautions. The initial incomplete-archive history is preserved here, not as an active blocker. The PowerShell script retains restore/Release build/unit/integration commands and fail-fast errors; its stale missing-source diagnostic is removed. Shell verification and smoke scripts are unchanged.

## Verification results for this revision

| Check | Result |
| --- | --- |
| `dotnet build Provena.sln --configuration Release` | Blocked, exit 127: dotnet command not found; compilation not started |
| `dotnet test Provena.sln --configuration Release` | Blocked, exit 127: dotnet command not found; tests not executed |
| `bash scripts/verify.sh` | Blocked, exit 127 at dotnet restore |
| `pwsh -File scripts/verify.ps1` | Blocked, exit 127: pwsh command not found; PowerShell runtime unverified |
| Environment inventory | dotnet, pwsh and docker not found on PATH |
| Source/migration/test/config comparison | Preserved byte-for-byte from the second attachment; only original README.md and scripts/verify.ps1 changed |
| Documentation and procedures | Local Markdown links resolve; skill frontmatter checked; no obsolete active missing-persistence instruction remains |
| Packaging | ZIP CRC and complete file-byte comparison checked |

These checks do not establish package availability, C# compilation, PowerShell execution, model/snapshot equality or successful schema migration. The repository source gap is resolved; the current runtime-verification limitation is the tool environment. No unavailable check counts as a pass.

## Exact file manifest relative to second attachment

Modified: `README.md`, `scripts/verify.ps1`.

Created:

- `AGENTS.md`
- `docs/PRODUCT.md`
- `docs/CODEBASE_MAP.md`
- `docs/EUDR_DOMAIN.md`
- `docs/ARCHITECTURE.md`
- `docs/DATABASE.md`
- `docs/API.md`
- `docs/SECURITY.md`
- `docs/TESTING.md`
- `docs/DEPLOYMENT.md`
- `docs/AI_DEVELOPMENT_GUIDE_HR.md`
- `docs/AI_DISCOVERY_REPORT.md`
- `docs/adr/README.md`
- `docs/adr/TEMPLATE.md`
- `docs/adr/0001-single-api-service-architecture.md`
- `docs/adr/0002-supplier-producer-production-plot.md`
- `docs/plans/TEMPLATE.md`
- `.agents/skills/database-change/SKILL.md`
- `.agents/skills/api-contract-change/SKILL.md`
- `.agents/skills/eudr-domain-change/SKILL.md`
- `.agents/skills/security-sensitive-change/SKILL.md`
- `.agents/skills/verify-change/SKILL.md`

## Next step

Run the verification script locally with .NET 10 and Docker and diagnose any actual failures. Before a schema feature, address F14 without changing historical schema behaviour. Then give your coding agent (Codex or Claude Code) a narrow feature and acceptance criteria, starting from AGENTS.md. Practical Croatian prompts are in [AI_DEVELOPMENT_GUIDE_HR](AI_DEVELOPMENT_GUIDE_HR.md).
