# ADR 0007: Case dossier, versioned lot facts and producer/plot source attribution

- Date: 2026-10-13
- Decision status: Accepted
- Implementation status: Implemented. The persistence model (`CaseDossier`, `CaseLot`, `LotContribution`, `ContributionPlot`), guards, `Current()`, migration `CaseDossier`, the internal API (`CaseDossierService`, three routes), the shipment lock and the export additions.
- Decision owner / approval evidence: The owner approved the case-dossier/lot-sources implementation plan supplied with the corresponding implementation request (2026-10-13, revision 4): D1-D5 approved, revision-2 corrections 1-5 and the revision-3 semantic correction incorporated. The original plan artifact was not retained in the repository.
- Amendments: Production Coverage & Lot Concurrency v1 (section "Amendment" below, commit `a9ded9a`).
- Supersedes / superseded by: refines [ADR 0002](0002-supplier-producer-production-plot.md) and builds on [ADR 0005](0005-retained-evidence-append-only-rows.md) and [ADR 0006](0006-production-provenance-plot-revisions.md) (supersedes none of them).

## Context and evidence

ADR 0006 created tenant-owned provenance identities (Producer -> ProductionPlot -> immutable PlotRevision) with no link to any case. There was no factual chain from a case's physical goods to identified producers, their quantities, production periods and the exact plot locations used. `GeoSubmission` is supplier evidence and is deliberately unrelated to plots. Source: `Domain/Dossiers/CaseDossier.cs`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Data/Migrations/*CaseDossier*`.

## Decision

- **`CaseDossier` is the stable case-owned anchor representing the case's one physical lot.** At most one per case, fully immutable after insert, never deleted, no business fields. A lot version is only ever inserted under its own case's dossier, so no physical lot can be moved between dossiers. Multi-lot cases, lot transfer, split, merge and processing/conversion are unsupported.
- **`CaseLot` rows are immutable versions of the facts about that lot** (sequential `Number`, `LotReference`, `Quantity`, `Unit`, `Note`). The only update is the one-way `SupersededAtUtc` retirement; exactly one version is current once a dossier has a lot (at most one is enforced by a partial unique index).
- **`LotReference` is a versioned operator business/batch reference, not a permanent physical-lot identity.** It is unique only among *current* lots in an organization (partial unique `(OrganizationId, LotReference) WHERE SupersededAtUtc IS NULL`, exact case-sensitive trimmed comparison). Superseded references may be reused, a case may keep its reference across versions, and another organization may use the same value. Consequence: a Completed/Archived case's current lot keeps its reference reserved, because terminal cases cannot save a new version.
- **Quantity is per producer, not per plot.** A `LotContribution` (unique per lot version and producer) states the portion of the lot's own material contributed by that producer, in the lot's unit; `numeric(20,6)`. There is no `SupplierId` and no supplier/producer relationship is validated or inferred. A `ContributionPlot` attributes a contribution to one plot at one **pinned** `PlotRevision` and carries no quantity.
- **Reconciliation (API rule, derived on read, not stored):** exact decimal equality of the summed contributions with the lot quantity, identical units (no conversion, `Other` rejected); under-allocation is storable, over-allocation is rejected; no tolerance. *Amended by [ADR 0010](0010-quantity-reconciliation-v1.md): the lot quantity is now also compared with the shipment and supplier-confirmed quantities by the completeness policy (pilot single-lot assumption); this contributions-to-lot rule itself is unchanged.*
- **Database-enforced provenance chain.** `ContributionPlot` stores a denormalized `ProducerId` and three composite FKs, backed by new alternate keys `LotContributions (OrganizationId, Id, ProducerId)`, `ProductionPlots (OrganizationId, ProducerId, Id)` and `PlotRevisions (OrganizationId, ProductionPlotId, Id)`: the link's producer is the contribution's producer, the plot belongs to that producer, and the revision belongs to that plot. The service validates first for clear 400s; the schema is the backstop.
- **Optional per-source production period** (`ProductionStartDate`/`ProductionEndDate`, SQL `date`, nullable). The rules (both or neither, start <= end, a single date is start == end) are API-validator rules, not SQL checks. The dates are factual source data, not a completeness or legal-sufficiency claim and not compared with the EUDR cut-off date or plot revision dates.
- **Active / carry-over rule (API):** a contribution or plot link that is new relative to the current lot version needs an active producer and plot (409); identity of a carried-over source is producer + plot + exact `PlotRevisionId`, and changing quantity or period on a carried-over source is not a new attribution. Superseded revisions may be pinned.
- **Shipment lock:** a shipment is not editable once its case has a dossier (the existing 409, checked under the case lock after the case lock is taken, so it serializes with dossier saves). This is not a staleness system; shipment creation and archiving are unchanged.
- **Order-independent idempotency:** an identical PUT (contributions keyed by producer, links by plot + revision) creates no version and no audit event.
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
- Plot geometry and production periods appear in the internal export (`plots/*.geojson`, manifest `dossier`); retention/erasure remains the open legal item.
- Rollback of the migration is possible only while the four dossier tables are empty (Down guard).
- Adding the two alternate keys is the only change to existing tables.
- Names and active flags in future DTOs are live master data, not snapshotted.

## Implementation and verification

Source: `Domain/Dossiers/CaseDossier.cs`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Data/Migrations/*CaseDossier*`. Tests: `DossierModelTests` (guards, indexes, raw-SQL chain and cross-organization FK violations), `MigrationTests` (schema, upgrade, violations, Down guard). API: `Services/CaseDossierService.cs`, `Endpoints/Cases/CaseDossierEndpoints.cs`, `Services/ShipmentService.cs` (lock), `Services/ExportService.cs`; tests `CaseDossierTests`, `DossierDomainTests`, the endpoint inventory/OpenAPI pins.

## Amendment: Production Coverage & Lot Concurrency v1

- Date: 2026-10-22. Implementation commit `a9ded9a` ("feat(api): add production coverage and lot concurrency"). Decision status: Accepted per the owner's implementation request; Implementation status: **Implemented** (backend only). This is an evolution of the same architecture, so it is recorded here and not in a new ADR.
- **No new entity.** The provenance model remains `Producer`, `ProductionPlot`, `PlotRevision`, `CaseDossier`, `CaseLot`, `LotContribution` and `ContributionPlot`. A `ProductionSource` or `ProductionEvent` would duplicate `ProductionPlot` + `PlotRevision` (source and location) and the per-`ContributionPlot` production period, and was rejected. Supplier != Producer != ProductionPlot is unchanged.
- **Derived `ProductionCoverage`.** `Services/ProductionCoverage.cs` derives, on read and never stored: the distinct source `countries` (from the pinned `PlotRevision`s), `periodFrom` (minimum start), `periodTo` (maximum end), `sourcesWithoutPeriod` (links missing either date), `producerCount` (contributing producers, also those without plot links) and `plotCount` (distinct plots). Every input is a fact the dossier fingerprint already covers. Production country stays source-level data: no case-level country field exists and the supplier, shipment-origin and legacy-submission countries are not production countries. It states no legal conclusion. It is a member of `CaseDossierDto` (`GET dossier`; in the working export only at `dossier.productionCoverage`, with no top-level copy) and, for the reviewed export, a sibling of the stored snapshot (`ProductionCoverage`, `ProductionCoverageScope`) derived from the snapshot's lot facts only.
- **Lot concurrency.** `CaseLotInput` gains `ExpectedCurrentLotId` (`null`: no lot was loaded). Under the case lock, after validation and before the identical-resubmission no-op, a value different from the current `CaseLot.Id` is 409 with `errors.reason` `LotHistoryChanged`, `currentLotId` and `currentLotNumber` (both null when no lot exists); nothing is written or audited and there is no silent retry. `lotReference` uniqueness is unchanged.
- **Advisory.** `PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION` (`area` `Production`, `subjectId` the current submission id) is emitted by `CompletenessService.Advisories` when the derived countries are not exactly the single `SupplierSubmission.CountryOfProductionCode` (BR + CO against BR differs; case and padding are ignored). It is not emitted when either side is absent and it never compares with `Shipment.OriginCountryCode`. It is informational evidence-inconsistency information: not a completeness item, outside `dataCoverage`, no review precondition, no compliance finding and not fingerprinted.
- **Fingerprint and snapshot unchanged.** `FingerprintVersion` and the snapshot schema stay 1 and nothing was added to either; a new `CaseLot` version stales the review through the existing `Lot` section. A later `PlotRevision` or a producer rename changes neither a pinned `ContributionPlot` nor a stored snapshot, and the reviewed coverage is never resolved from live rows.
- **No migration.** No entity, column, index or constraint was added or changed; the model snapshot is unchanged (pinned by a test).
- **Not solved (still open):** the Supplier-Producer relationship, producer/plot duplicate identity rules, legal production-date windows, geometry precision/area policy, `GeoSubmission` to plot conversion, per-plot quantity allocation, country risk, legality and deforestation findings, Annex I scope and split/merge/processing lineage. Multi-lot stays unsupported.
- Client handoff: [production coverage and lot concurrency](../frontend-handoffs/2026-10-production-coverage-and-lot-concurrency.md).

## Revisit trigger

Multi-lot cases or lot transfer, a supplier/producer relationship, per-plot allocation or unit conversion, or a ruling on whether production periods and reconciliation are mandatory for sufficiency.
