---
name: database-change
description: Use for EF entities, relationships, tenant queries, mappings, indexes, persistence rules or migrations in Provena.
---

# database-change

Paths below are relative to the repository root. Load this procedure only for a matching task.

1. Read `docs/DATABASE.md`, then affected entities/callers, `Data/AppDbContext.cs`, `Data/DesignTimeFactory.cs` and `Data/Migrations/` under `src/Provena.Api`. Inspect actual constraints, filters and the save overload used.
2. Identify current and desired cardinality, ownership, nullability, timestamps, deletion and uniqueness. Use domain context only where changed. Ask for unresolved business/data-loss decisions.
3. Inspect read filters and write enforcement separately, including foreign references and raw SQL. Preserve per-case transaction/locking where used; inspect DTO exposure.
4. Update entity/mapping together; generate a migration in `Data/Migrations` using the pinned EF tool. First resolve the documented coupling: InitialCreate calls `AppDbContextModelSnapshot.BuildInitialModel`; preserve the original target model when the snapshot is regenerated. Inspect Up/Down/snapshot and SQL for unrelated/destructive changes. Never use EnsureCreated to bypass migrations.
5. Test clean schema and upgrade with existing data on PostgreSQL, constraints and cross-tenant denial. Check snapshot drift. Record rollback/rollout limits.
6. Update DATABASE only for changed conventions, API for exposed contract changes. Run the relevant `verify-change` procedure and report actual evidence.

Output: model/tenant implications, migration/backfill decision, changed files, tests and any blocker. Commands and prerequisites live in DATABASE and TESTING, not here.
