# Frontend handoffs

This directory contains focused handoff documents for backend changes that
require adaptation in the separate Provena frontend repository.

Backend code, executable tests, OpenAPI and canonical backend documentation
remain the source of truth. These handoff documents summarize externally
observable changes for client implementation.

## When to create a handoff

Create or update a handoff when a backend change affects frontend behaviour,
including:

- endpoint additions or removals,
- request or response contract changes,
- status code or ProblemDetails changes,
- validation rules,
- enum or value-set changes,
- pagination, filtering, search or ordering,
- authentication or authorization behaviour,
- upload or download contracts,
- workflow or state-transition behaviour,
- public supplier portal behaviour.

Do not create a handoff for purely internal backend changes with no client-visible
effect.

## Naming

Use:

`YYYY-MM-<short-change-name>.md`

Examples:

- `2026-10-catalog-pagination.md`
- `2026-10-supplier-evidence-status.md`
- `2026-11-case-export-contract.md`

Prefer one handoff per coherent backend change.

## Required content

Each handoff should contain:

1. Summary
2. Changed endpoints or externally observable behaviour
3. Request/query contract
4. Response contract
5. Error/status behaviour
6. Breaking-change notes
7. Required frontend adaptations at contract level
8. Compatibility risks
9. Out-of-scope items
10. Backend verification performed
11. Local frontend/backend compatibility evidence: both checkout revisions/dirty state, exact command, result or an explicit not-run reason

Include representative JSON examples when they materially clarify the contract.

## Boundaries

Do not assume the backend agent knows the structure of the frontend repository.

Describe what the client must adapt to, not which frontend files or components
must be changed, unless a cross-repository contract explicitly defines them.

## Executable local check

Follow [CHANGE_WORKFLOW](../CHANGE_WORKFLOW.md) and the companion client `docs/LOCAL_TESTING.md`. The client live suite exercises the actual local API; mocked browser tests are a separate UI check. Do not add CI/CD or deployment: the owner has deferred them. Existing historical handoffs are not retroactively passing compatibility reports.
