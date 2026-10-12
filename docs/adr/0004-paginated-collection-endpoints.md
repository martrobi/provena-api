# ADR 0004: Offset pagination for unbounded tenant-wide list endpoints

- Date: 2026-10-12
- Decision status: Accepted
- Implementation status: Implemented
- Decision owner / approval evidence: Owner request on 2026-10-12 to add consistent server-side pagination to list endpoints that can realistically grow large, using the cases list as the starting convention. The owner approved the written plan with two refinements: the helper requires an ordered query, and tests do not duplicate paging edge cases per endpoint.
- Supersedes / superseded by: Replaces the earlier `docs/API.md` rule "Cases alone use `PageResult<T>` ... Do not silently apply case pagination to all endpoints".

## Context and evidence

Only `GET /api/eudr-cases` was paginated. `GET /api/suppliers`, `/api/products` and `/api/shipments` returned every tenant row, although CSV imports accept up to 10,000 rows per file. Cases already used `PageResult<T>` (`Contracts/Requests.cs`) with page default 1, size default 25, max 100.

## Decision

- Tenant-wide internal list endpoints that can grow without bound return `PageResult<T>` (`items`, `page`, `pageSize`, `totalCount`) with `page >= 1` and `pageSize` 1–100 (default 25). Currently: cases, suppliers, products, shipments.
- Paging goes through `Common/Paging.cs`. `ToPageAsync` accepts only `IOrderedQueryable<T>`, so ordering is part of the helper's API. Callers end the ordering with a unique tiebreak (`Id`); the type system cannot check uniqueness.
- Small nested collections (case documents, requirements, audit events), the dashboard and other lookup-style or bounded results stay unpaginated unless a concrete growth reason is documented.
- Item shapes are unchanged. No new filters and no schema change.

## Alternatives considered

- **Opt-in paging (array unless `page` is supplied).** Rejected: it leaves the unbounded query in place and gives each endpoint two response shapes.
- **Keyset/cursor pagination.** Not selected: the cases list already uses offset paging, and a second convention adds complexity at the current scale.
- **Paginating every collection.** Rejected: nested collections are small and bounded by their parent.

## Consequences

- Breaking change: the suppliers, products and shipments lists change from a JSON array to a page object, and clients that need all rows must page. No frontend is supplied, so client impact is untested.
- Suppliers and products order by `Name` then `Id`; shipments by `CreatedAtUtc` descending then `Id`.
- Ordering by `Name` or `CreatedAtUtc` has no supporting index. This is acceptable at current scale; an index would be a separate database change.
- Offset paging can skip or repeat rows when data changes between page requests.
- `totalCount` is computed after the tenant query filter, so it does not reveal other tenants' rows.

## Implementation and verification

Source: `src/Provena.Api/Common/Paging.cs`, `Services/CaseService.cs`, `CatalogService.cs`, `ShipmentService.cs` and the matching endpoint modules. Tests: pagination tests in `tests/Provena.Api.IntegrationTests/HttpContractTests.cs` plus the existing case list assertions. Test results belong in the report for the change that implemented this, not in this record.

## Revisit trigger

Revisit if list sizes make offset paging slow (consider indexes or keyset paging), or if a client needs stable iteration under concurrent writes.
