# ADR 0006: Production provenance as Producer, producer-scoped ProductionPlot and immutable PlotRevisions

- Date: 2026-10-13
- Decision status: Accepted
- Implementation status: Partial. The persistence model, guards, plot geometry validation and migration `ProductionProvenance` are implemented. The internal API and the attribution of case lots/quantities to plot revisions are not implemented.
- Decision owner / approval evidence: Owner approval of `docs/plans/2026-10-production-provenance.md` (2026-10-13): D1 approved with clarification (producer-scoped plot registration), D2 approved as recommended, D3-D5 approved.
- Supersedes / superseded by: refines [ADR 0002](0002-supplier-producer-production-plot.md) (does not supersede it).

## Context and evidence

ADR 0002 accepted Supplier != Producer != ProductionPlot but only Supplier existed. Case geometry is a supplier-submitted `GeoSubmission` ([CaseFiles.cs](../../src/Provena.Api/Domain/Files/CaseFiles.cs)) with no producer or plot identity. A later slice must attribute case lots and quantities to specific plot locations without reinterpreting existing evidence, which needs stable identities and immutable location history first.

## Decision

- **Three tenant-owned entities** (`Domain/Production/`): `Producer`, `ProductionPlot` and `PlotRevision`, each a `TenantEntity` with the query filter, organization FK and `(OrganizationId, Id)` alternate key, so later slices can composite-FK to them.
- **ProductionPlot is a stable producer-scoped plot registration**, not a canonical identity of the physical parcel. Each plot belongs to exactly one producer and `ProducerId` is immutable (guard-enforced). A producer or tenure change creates a new plot; the old one is retained and can be deactivated. Shared or multi-producer plots are deferred, with no claim that adding them is non-breaking.
- **PlotRevision holds all material location data** (country of production, geometry type Point/Polygon/MultiPolygon, normalized geometry JSON, optional declared area in hectares). The plot row holds none. Exactly one revision is current (`SupersededAtUtc IS NULL`, enforced as *at most one* by a partial unique index); revisions are immutable apart from the one-way supersede, following [ADR 0005](0005-retained-evidence-append-only-rows.md). Unlike ADR 0005 a sequential `Number` per plot is stored (unique per plot) because there are no legacy rows to account for.
- **A revision is created only for a material change**: geometry, country of production or declared area. Geometry equality is topological (NetTopologySuite `EqualsTopologically` on validated geometries), so ring start, orientation, MultiPolygon component order and a single-component MultiPolygon vs the identical Polygon are not changes. Country and declared area use exact value equality. A note alone is not material. The comparison is `GeoJsonValidationService.IsMaterialChange`; a rejected no-op is a service rule for the endpoint slice.
- **Active-state rules** (service rules for the endpoint slice): plot creation requires an active producer (decided under a producer row lock); revision creation requires an active plot (decided under a plot row lock) and does not check the producer; deactivating a plot does not supersede its current revision.
- **No physical delete** of producers, plots or revisions: identities are deactivated, revisions superseded. The persistence guard rejects deletes; Restrict FKs are the database backstop for later references.
- **Plots stay separate from `GeoSubmission`.** A `GeoSubmission` is case-scoped supplier evidence; a `PlotRevision` is tenant master provenance with no review status. Existing case geometry is not read, converted or backfilled, and Supplier is never inferred to be a Producer.
- **Structural validity only.** `ValidatePlotGeometry` accepts one bare Point, Polygon or MultiPolygon geometry object with the same range/closure/topology checks as case uploads and a 5 MiB size cap. This is not EUDR completeness or sufficiency of the geolocation: there is no 4 ha point/polygon rule, no precision rule and no computed area. Case uploads still reject Point.

Deliberately undecided: supplier/producer relationship, case-lot/quantity attribution, EU GeoJSON profile conformance, retention/erasure of producer and plot data (the open legal item now covers them), and reviewed dossier snapshots.

## Alternatives considered

- **Geometry on the plot row with an audit trail.** Rejected: later references need a stable id of the exact location that was used.
- **Plots independent of producers / many-to-many.** Rejected for the pilot: tenure changes and shared parcels are undecided domain evolution.
- **Promote GeoSubmission features to plots.** Rejected: ADR 0002 forbids equating an uploaded feature with an identified plot, and legacy evidence carries no identity.
- **Text or `EqualsExact` geometry comparison.** Rejected: jsonb reorders keys and exact comparison treats equivalent representations as changes.
- **Generic versioning/event sourcing.** Rejected, as in ADR 0005.

## Consequences

- Producer master data is overwritten on edit (audited by id only, once the API exists); plot location history is retained.
- Plot locations may relate to natural persons (smallholders): never logged, never exposed publicly; retention/erasure is an open legal item.
- Rollback of the migration is possible only while the three tables are empty (Down guard).
- The guard is application-level only, as for retained evidence; other save overloads, bulk writes and raw SQL bypass it.

## Implementation and verification

Source: `Domain/Production/*`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Services/GeoJsonValidationService.cs`, `Data/Migrations/*ProductionProvenance*`. Tests: `DomainTests` (plot geometry, material change), `ProvenanceModelTests`, `MigrationTests`. The endpoint slice adds `ProvenanceService`, DTOs and routes.

## Revisit trigger

A requirement for shared plots, a supplier/producer relationship, plot ownership across tenants, or a legal ruling on geolocation precision/sufficiency or retention.
