# ADR 0007: Case dossier, versioned lot facts and producer/plot source attribution

- Date: 2026-10-13
- Decision status: Accepted
- Implementation status: Partial. The persistence model (`CaseDossier`, `CaseLot`, `LotContribution`, `ContributionPlot`), guards, `Current()` and migration `CaseDossier` are implemented. The internal API, the shipment lock and the export additions arrive with the next commit and are **not** callable yet.
- Decision owner / approval evidence: Owner approval of `docs/plans/2026-10-case-dossier-lot-sources.md` (2026-10-13, revision 4): D1-D5 approved, revision-2 corrections 1-5 and the revision-3 semantic correction incorporated.
- Supersedes / superseded by: refines [ADR 0002](0002-supplier-producer-production-plot.md) and builds on [ADR 0005](0005-retained-evidence-append-only-rows.md) and [ADR 0006](0006-production-provenance-plot-revisions.md) (supersedes none of them).

## Context and evidence

ADR 0006 created tenant-owned provenance identities (Producer -> ProductionPlot -> immutable PlotRevision) with no link to any case. There was no factual chain from a case's physical goods to identified producers, their quantities, production periods and the exact plot locations used. `GeoSubmission` is supplier evidence and is deliberately unrelated to plots. Source: `Domain/Dossiers/CaseDossier.cs`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Data/Migrations/*CaseDossier*`.

## Decision

- **`CaseDossier` is the stable case-owned anchor representing the case's one physical lot.** At most one per case, fully immutable after insert, never deleted, no business fields. A lot version is only ever inserted under its own case's dossier, so no physical lot can be moved between dossiers. Multi-lot cases, lot transfer, split, merge and processing/conversion are unsupported.
- **`CaseLot` rows are immutable versions of the facts about that lot** (sequential `Number`, `LotReference`, `Quantity`, `Unit`, `Note`). The only update is the one-way `SupersededAtUtc` retirement; exactly one version is current once a dossier has a lot (at most one is enforced by a partial unique index).
- **`LotReference` is a versioned operator business/batch reference, not a permanent physical-lot identity.** It is unique only among *current* lots in an organization (partial unique `(OrganizationId, LotReference) WHERE SupersededAtUtc IS NULL`, exact case-sensitive trimmed comparison). Superseded references may be reused, a case may keep its reference across versions, and another organization may use the same value. Consequence: a Completed/Archived case's current lot keeps its reference reserved, because terminal cases cannot save a new version.
- **Quantity is per producer, not per plot.** A `LotContribution` (unique per lot version and producer) states the portion of the lot's own material contributed by that producer, in the lot's unit; `numeric(20,6)`. There is no `SupplierId` and no supplier/producer relationship is validated or inferred. A `ContributionPlot` attributes a contribution to one plot at one **pinned** `PlotRevision` and carries no quantity.
- **Reconciliation (API rule, derived on read, not stored):** exact decimal equality of the summed contributions with the lot quantity, identical units (no conversion, `Other` rejected); under-allocation is storable, over-allocation is rejected; no tolerance.
- **Database-enforced provenance chain.** `ContributionPlot` stores a denormalized `ProducerId` and three composite FKs, backed by new alternate keys `LotContributions (OrganizationId, Id, ProducerId)`, `ProductionPlots (OrganizationId, ProducerId, Id)` and `PlotRevisions (OrganizationId, ProductionPlotId, Id)`: the link's producer is the contribution's producer, the plot belongs to that producer, and the revision belongs to that plot. The service will validate first for clear 400s; the schema is the backstop.
- **Optional per-source production period** (`ProductionStartDate`/`ProductionEndDate`, SQL `date`, nullable). The rules (both or neither, start <= end, a single date is start == end) are API-validator rules, not SQL checks. The dates are factual source data, not a completeness or legal-sufficiency claim and not compared with the EUDR cut-off date or plot revision dates.
- **Active / carry-over rule (API, next commit):** a contribution or plot link that is new relative to the current lot version needs an active producer and plot (409); identity of a carried-over source is producer + plot + exact `PlotRevisionId`, and changing quantity or period on a carried-over source is not a new attribution. Superseded revisions may be pinned.
- **Shipment lock (next commit):** a shipment is not editable once its case has a dossier (the existing 409, checked under the case lock). This is not a staleness system. Until that commit lands the shipment rules are unchanged.
- **Order-independent idempotency (next commit):** an identical PUT (contributions keyed by producer, links by plot + revision) creates no version and no audit event.
- **No link to `GeoSubmission`.** Case geometry stays supplier evidence; it is never read, converted or backfilled into plots or lot sources. No backfill and no fabrication from shipments or submissions.
- **Persistence guard:** `CaseLot` joins the `Retirement` map; `CaseDossier`, `LotContribution` and `ContributionPlot` reject any `Modified` or `Deleted` entry. Application-level only, as in ADR 0005/0006: other save overloads, bulk writes and raw SQL bypass it; Restrict FKs are the database backstop.

Deliberately undecided: applicability, Article 9 completeness (including whether production periods are required), risk/mitigation, release/approval, reviewed dossier snapshots and review-decision history, DDS semantics, supplier-producer relationships, multi-lot cases, unit conversion, tolerance, per-plot quantities, registering plots from case geometry.

## Alternatives considered

- **`LotReference` as permanent global physical-lot identity.** Rejected by the owner: it is an operator-entered business reference that can change; the dossier carries identity.
- **Mutable lot row with an audit trail.** Rejected: later slices need stable ids of the exact facts that were recorded (same reasoning as ADR 0005/0006).
- **Per-plot quantities / unit conversion / tolerance.** Deferred: net-mass vs supplementary-unit, yield and conversion policy are undecided domain questions.
- **Service-only plot/producer/revision consistency.** Rejected: the chain is cheap to enforce with composite FKs and must hold for any write path.
- **SQL CHECK constraints for the production period.** Rejected, consistent with the convention that value rules live in validators.
- **Linking lot sources to `GeoSubmission`.** Rejected: ADR 0002 forbids equating an uploaded feature with an identified plot.

## Consequences

- Plot locations used by a case can never be rewritten by later plot edits; superseded revisions stay valid targets.
- Plot geometry and production periods will appear in the internal export (next commit); retention/erasure remains the open legal item.
- Rollback of the migration is possible only while the four dossier tables are empty (Down guard).
- Adding the two alternate keys is the only change to existing tables.
- Names and active flags in future DTOs are live master data, not snapshotted.

## Implementation and verification

Source: `Domain/Dossiers/CaseDossier.cs`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Data/Migrations/*CaseDossier*`. Tests: `DossierModelTests` (guards, indexes, raw-SQL chain and cross-organization FK violations), `MigrationTests` (schema, upgrade, violations, Down guard). Service, API, shipment lock and export follow in the next commit; this ADR is updated then.

## Revisit trigger

Multi-lot cases or lot transfer, a supplier/producer relationship, per-plot allocation or unit conversion, or a ruling on whether production periods and reconciliation are mandatory for sufficiency.
