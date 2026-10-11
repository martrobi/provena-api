# Provena agent entry point

Provena is a .NET 10 controller-based API for collecting and reviewing EUDR workflow evidence. It does not determine legal compliance or submit a DDS.

Read this file first; **do not load all documentation or skills by default**. Choose the smallest relevant route below, then inspect the named implementation and its tests. Paths are relative to the repository root.

## Agent compatibility

This `AGENTS.md` is the canonical repository instruction source for OpenAI Codex and Claude Code. Do not duplicate these project rules in tool-specific instruction files. If a tool-specific adapter is ever needed, it must reference/import this file and contain only tool-specific behaviour.

The procedures under `.agents/skills/*/SKILL.md` are also canonical. Follow the routed procedure when this file points to it even if the current client does not auto-discover `.agents/skills` as a native skills directory.

## User intent and repository-level changes

Interpret user requests by intent, not by the files explicitly mentioned.

The user is not expected to know which source files, tests, documentation, agent instructions, skills, migrations, scripts, or configuration files must change.

Treat every implementation task as a repository-level change. Determine the complete coherent set of changes required to satisfy the request while keeping the repository internally consistent.

Do not stop at the first obvious source-code edit when the requested behaviour or architecture requires coordinated changes elsewhere.

For each task:

1. inspect the relevant existing implementation and tests,
2. identify all affected repository artifacts,
3. read the smallest relevant documentation and routed skills,
4. implement the requested behaviour,
5. update tests and supporting artifacts that are made stale by the change,
6. run the appropriate verification,
7. review the final diff for consistency and unintended changes.

Ask the user only when a genuine product, legal, security, domain, or architectural decision is unresolved and cannot be determined from the repository or the explicit request. Do not ask the user to identify implementation files or documentation files that can be discovered from the repository.

## Change propagation

When implementing a requested change, update every repository artifact that must change to keep the project correct and internally consistent.

Depending on the task, this may include:

- application source code,
- tests,
- API contracts,
- database schema and migrations,
- architecture documentation,
- domain documentation,
- security documentation,
- testing documentation,
- deployment/configuration documentation,
- codebase maps,
- ADRs,
- scripts,
- `AGENTS.md`,
- relevant `.agents/skills/*/SKILL.md` procedures.

Do not wait for the user to explicitly request these follow-up edits.

If the implementation makes an existing instruction, architecture rule, convention, example, procedure, or documentation statement outdated, update it as part of the same task.

Do not update unrelated documentation or agent instructions merely for completeness. Changes must be caused by the requested task.

## Persistent decisions

When a user request establishes or changes a durable project convention, architecture rule, API convention, persistence convention, security rule, domain rule, or development workflow:

1. implement the change,
2. update the owning canonical documentation,
3. update `AGENTS.md` if future agents need to know the rule,
4. update relevant skills if their procedure is no longer correct,
5. add or update tests that enforce the decision where practical,
6. add or update an ADR when the decision is significant enough to require durable architectural rationale.

Examples include:

- replacing MVC Controllers with Minimal APIs,
- changing endpoint organization or API error conventions,
- changing tenant isolation rules,
- changing authentication or authorization architecture,
- introducing a new persistence or migration convention,
- changing folder/module boundaries,
- changing the repository verification workflow.

The repository must not be left in a state where the source code follows one architecture while documentation or agent instructions describe another.

An explicit user request may intentionally replace an existing project rule. In that case, do not preserve the old rule merely because it appears in this file or another document. Treat the requested change as the new intended direction, surface any important consequences, and update the stale rule when implementation proceeds.

## Evidence and authority

For current behaviour, source code and executable tests are primary evidence; an unrun test is an expectation, not proof. Explicit owner-approved domain/security decisions govern intended changes. Documentation describes these; plans describe proposed changes and never silently override current behaviour. A task can explicitly request a change, but conflicts with an approved rule must be surfaced. Label observations, project rules, recommendations and unknowns separately. Do not normalize a security defect into a rule.

Persistence lives in `src/Provena.Api/Data/AppDbContext.cs` and `Data/Migrations/`. Read [DATABASE.md](docs/DATABASE.md) for tenant filters/write guards, constraints and the initial migration/snapshot coupling before schema work. Source review is not a passing runtime test.

## Task routing

| Change                                          | Read only these initially                                    | Procedure when needed                                                          |
| ----------------------------------------------- | ------------------------------------------------------------ | ------------------------------------------------------------------------------ |
| Locate code / small local fix                   | [CODEBASE_MAP](docs/CODEBASE_MAP.md), affected source/test   | —                                                                              |
| Product scope                                   | [PRODUCT](docs/PRODUCT.md)                                   | —                                                                              |
| Service boundaries / DI / transactions          | [ARCHITECTURE](docs/ARCHITECTURE.md)                         | —                                                                              |
| Entity / query / schema / migration             | [DATABASE](docs/DATABASE.md)                                 | [database-change](.agents/skills/database-change/SKILL.md)                     |
| Endpoint / DTO / CSV / export                   | [API](docs/API.md)                                           | [api-contract-change](.agents/skills/api-contract-change/SKILL.md)             |
| Case state / evidence / producer / plot         | [EUDR_DOMAIN](docs/EUDR_DOMAIN.md)                           | [eudr-domain-change](.agents/skills/eudr-domain-change/SKILL.md)               |
| Auth / tenant / public token / upload / logging | [SECURITY](docs/SECURITY.md)                                 | [security-sensitive-change](.agents/skills/security-sensitive-change/SKILL.md) |
| Tests / verification                            | [TESTING](docs/TESTING.md)                                   | [verify-change](.agents/skills/verify-change/SKILL.md)                         |
| Runtime / configuration / containers            | [DEPLOYMENT](docs/DEPLOYMENT.md)                             | —                                                                              |
| Significant cross-cutting change                | Relevant rows above; [plan template](docs/plans/TEMPLATE.md) | Owner approves unresolved domain/architecture decisions                        |

Use [discovery findings](docs/AI_DISCOVERY_REPORT.md) only for baseline issues or technical-debt work. Human onboarding: [Croatian guide](docs/AI_DEVELOPMENT_GUIDE_HR.md). Important decisions: [ADR index](docs/adr/README.md).

## Project rules

- Follow the current API → scoped service → DbContext boundaries unless the task explicitly changes that architecture. Do not add repositories, CQRS, mediator, new projects or packages without a concrete approved need. Match nearby style; avoid incidental reformatting.
- Keep validation server-side, preserve cancellation, transactions, per-case locks and post-lock token rechecks. Queries returning tenant data must be tenant-scoped; verify enforcement, including writes and foreign references. Never trust client `OrganizationId`.
- Keep internal routes authenticated; the public capability grants access to one request/case only. No new `IgnoreQueryFilters` use without explicit security review. Never expose token hashes, storage keys, internal notes or audit payloads through public DTOs.
- Never log passwords, bearer tokens, public URLs, raw evidence or connection secrets. Do not commit secrets or uploaded data. Frontend checks do not authorize backend operations.
- Supplier evidence is not reviewer acceptance. Automated geometry/format validation is not regulatory verification. `ReadyForExport` and `Completed` are operational states. Preserve current transition order unless explicitly changing it.
- Supplier, Producer and ProductionPlot are distinct target concepts, not interchangeable entities; only Supplier exists today. Ask the owner about new relationships, legal interpretations, review permissions, retention or changed status semantics before implementing those decisions. Continue unrelated unambiguous work.
- Do not weaken tests, auth, tenant boundaries, warning-as-error settings or validation to get a green build. Do not bypass persistence guards through other SaveChanges overloads, bulk writes or raw SQL without explicit review and equivalent enforcement.

## Finish the change

For code/tooling changes, run `pwsh -File scripts/verify.ps1` (or `bash scripts/verify.sh`) with .NET 10 and Docker available. See TESTING for focused checks. No skipped check counts as a pass; report commands, exit status, blockers and residual risk. Documentation-only changes need path/content/diff checks, not fabricated runtime evidence.

Before considering a task complete, review the final diff and confirm that:

- the requested behaviour is implemented,
- affected tests are updated,
- stale documentation has been corrected,
- persistent rules are reflected in `AGENTS.md` when future agents need them,
- routed skills remain accurate,
- code moves are reflected in the codebase map when applicable,
- no unrelated findings were silently changed.

Update the owning document when public contracts, domain rules, security boundaries, persistence, configuration or verification commands change. Update the map when code moves. Add an ADR only for a durable significant decision. Trivial internal edits need no documentation ceremony.

Final report: behaviour changed, files changed, evidence/tests run, blockers, residual risk, and unresolved decisions. Never claim a check passed unless it was actually run successfully. Never silently fix unrelated findings.
