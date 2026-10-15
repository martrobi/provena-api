# Coordinated repository changes

Interpret the requested outcome across source, tests, contracts, schema/migrations, scripts and documentation. Update every artifact made stale by the change, but do not update unrelated documents for ceremony. The user need not identify implementation files.

## Durable decisions

Explicit owner decisions may replace earlier conventions. Surface their consequences; implement them coherently, update the owning document and relevant AGENTS routing/procedures, and add meaningful enforcement tests where practical. Add an ADR only for a significant durable architectural or domain decision. Plans never silently override approved rules or current code.

## Frontend handoffs


The frontend is maintained in a separate repository and is not automatically
available to backend agents.

When a backend change modifies an externally observable contract or behaviour
that a separate frontend must adapt to, create or update a focused frontend
handoff document under `docs/frontend-handoffs/`.

Typical triggers include:

- endpoint additions or removals,
- request or response shape changes,
- status code or ProblemDetails changes,
- validation rules,
- enum/value-set changes,
- pagination, filtering, search or ordering behaviour,
- authentication or authorization behaviour,
- upload/download contracts,
- workflow or state-transition changes,
- public supplier portal behaviour.

A frontend handoff must:

- describe the externally observable behaviour after the change,
- identify changed endpoints and request/response contracts,
- include representative examples where useful,
- clearly mark breaking changes,
- describe required client adaptations at the contract level,
- list relevant compatibility risks and intentional out-of-scope items,
- report the backend verification performed.

Do not prescribe frontend repository file names, component names or implementation
details unless they are part of an explicitly agreed cross-repository contract.

Backend code, executable tests and canonical API documentation remain the source
of truth. Frontend handoff documents are implementation handoffs, not a second
API specification.

Do not create a frontend handoff for purely internal changes with no externally
observable client impact.


## Local compatibility evidence

The companion client owns the executable cross-repository browser suite, documented in its `docs/LOCAL_TESTING.md`. For an affected shared workflow, run it against this exact backend checkout and record both repository revisions (and dirty state), environment, command and result in the handoff. Handoffs and mocked browser tests are not proof of live compatibility. If the client or local prerequisites are unavailable, report compatibility as not run. Do not add a CI workflow or deployment to obtain this evidence.
