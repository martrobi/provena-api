---
name: eudr-domain-change
description: Use for Provena case or requirement states, review, evidence meaning, supplier submissions, Producer or ProductionPlot changes.
---

# eudr-domain-change

Paths below are relative to the repository root. Load this procedure only for a matching task.

1. Read `docs/EUDR_DOMAIN.md` and affected methods/tests; read ADR 0002/0006 only if production concepts are involved. Distinguish implemented behaviour, explicit direction and suggestions.
2. Trace current mutation → requirement resets → status recalculation → audit → export effects. Include terminal exceptions and supplier completion semantics; do not infer rules from names.
3. Identify responsibility: supplier assertion, internal review, automated format check or external regulatory conclusion. Never turn a submission or valid polygon into compliance.
4. For new relationships, waiver permissions, retention, legal interpretation or altered state priority, surface a concrete owner decision with options/impact. Do useful unambiguous work while that decision is pending. Use a plan for significant model changes.
5. After approval, implement the narrow rule and test allowed/denied transitions, re-submission, terminal cases and tenant isolation as relevant. Route schema/contract work to those procedures.
6. Update the authoritative domain section and any durable ADR decision; label target vs implemented status honestly. Run `verify-change`.

Output: rule delta and approval basis, transition/evidence consequences, tests and deliberately unresolved questions.
