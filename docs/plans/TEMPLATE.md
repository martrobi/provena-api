# Plan: descriptive feature name

- Status: Draft / Approved / Implementing / Completed / Superseded
- Owner / approval evidence:
- Baseline commit or snapshot:
- Related ADRs / task:
- Open decisions (blocking which steps):

## Problem

Concrete user problem; affected workflow and evidence.

## Business goal

Observable outcome and why it matters.

## Current behaviour

Link the current source and tests. Separate verified behaviour, unrun expectations and unknowns.

## Desired behaviour

User-visible changes and explicit compatibility expectations.

## Domain rules

Approved rules, examples and edge cases. Mark proposals and questions; do not invent legal conclusions.

## Functional requirements

Numbered requirements traceable to acceptance criteria.

## Non-functional requirements

Relevant performance, reliability, concurrency, accessibility of API errors or operational constraints; avoid invented targets.

## Security implications

Authentication, permission, tenant boundaries, public capabilities, files, logs and sensitive data. Negative cases.

## Data model changes

Entities, relationships, cardinalities, ownership, indexes/constraints, history and timestamp implications.

## API changes

Routes, DTOs, enum values, status/errors, pagination, CSV/ZIP contracts and affected clients.

## Migration implications

Existing-data mapping, backfill, nullability, destructive operations, schema upgrade/downgrade risks. State missing baseline blockers.

## Acceptance criteria

Concrete Given/When/Then cases, including denial and failure outcomes; each must be verifiable.

## Implementation steps

Small coherent steps with dependencies and relevant files. No global refactor unless justified by the problem.

## Tests

Map each important criterion to unit/HTTP/PostgreSQL/upgrade checks; identify environment prerequisites.

## Out of scope

Related ideas deliberately excluded.

## Documentation impact

Which authoritative documents, routing entries or ADRs actually change? Trivial internals need no duplicate narrative.

## Rollout / compatibility concerns

Deployment order, old/new client coexistence, data safety, monitoring and rollback limitations. No production execution implicit.

## Completion evidence

Actual changes, commands/results, unresolved risks, final commit/PR; archive stale assumptions rather than letting them override source.
