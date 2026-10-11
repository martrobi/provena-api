---
name: verify-change
description: Use to verify and accurately report a Provena code or tooling change before delivery.
---

# verify-change

Paths below are relative to the repository root. Load this procedure only for a matching task.

1. Inspect the diff and scope. Read `docs/TESTING.md`; check required source, SDK and Docker availability. Distinguish repository incompleteness from environment failures.
2. For documentation-only work, check actual referenced paths, evidence/claims, links and that application/test files did not change. Do not invent runtime validation.
3. For code/tooling, run the repository gate: `pwsh -File scripts/verify.ps1` or `bash scripts/verify.sh`. This restores, builds Release and runs both test projects. Use focused tests for feedback when useful; rebuild before `--no-build` after code changes.
4. Use actual PostgreSQL integration tests for provider/query/tenant changes. Smoke is optional, requires a running disposable API and writes data; it is not a substitute for tests.
5. If a command fails, report its exact command, exit status and reason. No missing-SDK, skipped or unexecuted check is a pass. Do not weaken warnings/tests/auth, remove snapshot assertions or silently switch to an in-memory provider.
6. Confirm intended docs/contracts changed, no unrelated edits or secrets were introduced, and known blockers remain visible until resolved with evidence.

Output: changed behaviour and scope, commands/results (passed/failed/blocked/not run), remaining risk, and next actionable step. Full commands and test coverage live in TESTING.
