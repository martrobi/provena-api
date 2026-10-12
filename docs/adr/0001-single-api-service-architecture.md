# ADR 0001: Preserve the existing single-API service structure

- Date: 2026-10-11
- Decision status: Observed baseline
- Implementation status: Structure and persistence source present; runtime verification pending
- Approval evidence: Source-derived record; no historical owner approval claimed

## Context and evidence

`Provena.sln` has one API and two test projects. Controllers delegate to `Services/`, which depend directly on AppDbContext. Domain entities, contracts and infrastructure all reside in `src/Provena.Api`. Program.cs registers services directly. No repository layer, mediator or separate application/domain assemblies is supplied.

## Recorded choice

The baseline is a single deployable API with service-based orchestration and direct EF use. Routine AI work should follow that structure, per the owner's request not to redesign or add unnecessary abstractions. Revision note (2026-10-12): the second archive supplies the original persistence source. This corrects the earlier evidence gap without changing the recorded architecture. Revision note (2026-10-12, later): the HTTP entry layer described above as "Controllers" was replaced by feature-organized Minimal API endpoint modules per [ADR 0003](0003-minimal-api-http-layer.md). The single-API, service-orchestration and direct-EF choice recorded here is unchanged.

## Alternatives and consequences

Extra layers/projects, CQRS or generic repositories would be new architecture. No evidence establishes that these were previously evaluated or rejected. The current structure makes code easy to navigate at this size; direct EF access couples service tests to persistence and concentrates workflow rules in mutable service operations. Those tradeoffs are inferred from source, not invented historical rationale.

## Verification and revisit

See [ARCHITECTURE](../ARCHITECTURE.md), [DATABASE](../DATABASE.md) and [TESTING](../TESTING.md). Revisit only for a concrete boundary, scaling, ownership or testing problem with an owner-reviewed plan; folder aesthetics alone is insufficient.
