---
name: api-contract-change
description: Use for Provena endpoint, request or response DTO, HTTP error, pagination, enum, CSV or export contract changes.
---

# api-contract-change

Paths below are relative to the repository root. Load this procedure only for a matching task.

1. Read `docs/API.md`; locate affected endpoint module (`src/Provena.Api/Endpoints/<Feature>/`) → request/validator → service projection and HTTP tests. Inspect `scripts/smoke.py` if its flow is affected. The frontend is not supplied.
2. State the current and proposed contract: route, auth, fields/nullability, enum/string behaviour, errors/status codes and compatibility. Include CSV headers/ZIP fields when relevant. Ask about unapproved breaking changes.
3. Keep server validation and explicit public projections. Never expose entity internals just because an internal endpoint returns entities. Read SECURITY only if auth/tenant/token/files or sensitive data changes.
4. Make a narrow implementation; preserve cancellation, transactions and status rules. No new abstraction or API version scheme without a concrete decision.
   - Map new routes in the owning feature module's route group, never in Program.cs.
   - Keep handlers thin and return `TypedResults`.
   - Register new modules in `MapProvenaEndpoints()`.
   - Update the expected surface in `EndpointInventoryTests`.
   - New or changed tenant-wide list endpoints follow the pagination convention in `docs/API.md` (`Common/Paging.cs`, `PageResult<T>`, deterministic order with an `Id` tiebreak); changing a response to or from a page object is a breaking contract change.
   - Remember that query/form enum binding needs `EndpointConventions.ParseEnum` to stay case-insensitive.
5. Add meaningful HTTP tests for changed success and failure responses and relevant tenant denial. Test actual serialization, not only DTO constructors.
6. Update the owning API section and affected consumers/tests.
   - If the change has frontend impact, create or update the required `docs/frontend-handoffs/` document according to `AGENTS.md`.
   - Run `verify-change`.
   - Report client compatibility not tested separately from server tests.

Output: contract delta, compatibility decision, files/tests and remaining gaps.
