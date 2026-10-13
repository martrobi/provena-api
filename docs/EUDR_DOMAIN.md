# EUDR domain as represented in Provena

This is a description of the supplied implementation and explicit project direction, not a regulatory interpretation. Evidence: `Domain/`, `Services/ShipmentService.cs`, `EudrCaseStatusService.cs`, `CaseService.cs`, `SupplierRequestService.cs`, `CaseFileService.cs`, `ExportService.cs` beneath `src/Provena.Api/`. Declared database constraints and application guard limits are documented in [DATABASE](DATABASE.md); migrations/tests have not been executed here.

## Concepts today

An Organization owns operational tenant data. A Supplier is a commercial counterparty. A Product contains a CN code as a string (preserving leading zeroes), commodity enum and active flag. A Shipment references one supplier and one product, one quantity/unit, origin and optional dates. No shipment-line collection exists. A new shipment creates one EudrCase and six required EudrRequirements, initially Missing:

| Requirement | What the current submission path treats as present |
| --- | --- |
| SupplierInformation | Any valid SaveDetails request marks it Submitted, even if all optional fields are empty |
| CountryOfProduction | Nonempty country code; validator checks two uppercase letters, not a country registry |
| ProductionPeriod | Both from/to dates; validator checks order when both are provided |
| QuantityConfirmation | Positive confirmed quantity; not compared to shipment quantity |
| Geolocation | Accepted geometry file upload |
| SupportingDocuments | A document upload; category does not change the checklist type |

Evidence is retained, not overwritten ([ADR 0005](adr/0005-retained-evidence-append-only-rows.md)). _Withdrawn_ = a document removed from the current dossier by an internal user (row and bytes kept); _superseded_ = a geometry or supplier-details version replaced by a newer one. Exactly one geometry and one supplier-details row per case is _current_; workflows, review, export and the public portal see only current evidence. Version order is `CreatedAtUtc` then `Id`; there are no version numbers. Retained versions are not reviewer-accepted evidence, and review decisions are not yet linked to versions. SupplierSubmission stores one case-level production country, period, quantity and notes per version (the current one is used). SupplierRequest grants token access to the case and tracks sent/opened/completed/revoked/expiry timestamps. CaseDocument stores metadata and a file key; GeoSubmission stores one current geometry file plus normalized JSON and structural validation summary. AuditEvent records workflow events.

## Exact status priority (observed)

`EudrCaseStatusService.Calculate` applies the following order; do not reorder as a cleanup:

| Priority | Condition | Result |
| --- | --- | --- |
| 1 | Current state Completed or Archived | Preserve that state |
| 2 | No request with non-null SentAtUtc | Draft |
| 3 | Any required requirement Missing | WaitingForSupplier |
| 4 | Any requirement Submitted or Rejected (including optional) | ReviewNeeded |
| 5 | All required requirements Accepted or NotRequired | ReadyForExport |
| 6 | Otherwise | WaitingForSupplier |

Request-sent means **any historical request** with SentAtUtc; revoked/expired requests still count. The timestamp is set at creation even with the email stub. No supplier-completion timestamp is needed for readiness. With a sent request, all six requirements could be waived and yield ReadyForExport.

Reviewer input allows Accepted, Rejected or NotRequired. Accept/reject requires current Submitted; NotRequired bypasses that precondition and does not require a nonempty note. A new/changed piece of evidence clears review metadata and sets its requirement to Submitted or Missing, except NotRequired stays unchanged. Details updates reset four requirements together, even if values did not change. Document upload/withdrawal (DELETE) resets SupportingDocuments (Missing when no current document remains, otherwise Submitted); geometry upload resets Geolocation. Resubmitting after review keeps the earlier values as a superseded version, but the requirement's review decision (note/reviewer/time) is still cleared and not retained (known gap: review-decision history belongs to the later reviewed-snapshot slice). A resubmitted details save also clears supplier completion, so the case returns to waiting for the supplier.

Supplier completion rejects required Missing/Rejected items, sets timestamps and recalculates status; it does **not** accept requirements or lock further supplier edits. Public writes clear completion timestamps for that request/case. Repeated supplier completion can add repeated audit events. Internal completion recalculates then requires ReadyForExport, sets Completed and stores an optional external DDS reference without verifying it.

`EnsureEditable` blocks edits to Completed/Archived where called, including public GET via LockPublic. Exceptions to blanket immutability: archive can change Completed to Archived; revoke still operates on a terminal case; export remains allowed in every status and adds an audit event; catalog edits can change supplier/product data subsequently displayed/exported for an old case. Do not document historical snapshots or fully immutable completed cases.

Shipment updates are blocked once any supplier request exists or evidence has a status other than Missing/NotRequired. Archival retains records/evidence and revokes requests. There is no unarchive/reopen API. Internal document withdrawal does not explicitly clear supplier-completion timestamps. These are observations to review, not new endorsed business rules.

## Geometry boundaries

The validator supports Polygon/MultiPolygon directly or wrapped in Feature/FeatureCollection. It checks nonempty geometry, finite coordinates, longitude/latitude ranges, closed rings and topology with NetTopologySuite; Feature properties must be object/null. Point geometries are rejected for case uploads (plot geometry has its own validator, see below). A third coordinate is allowed and checked for finiteness but topology uses only the first two. FeatureCount counts geometry objects processed, not individual polygons inside a MultiPolygon and not production plots.

Invalid uploads return 400 rather than saving an Invalid GeoSubmission. Valid geometry means structural/topological validity only. It does not prove location authenticity, land ownership, production attribution, deforestation status or regulatory sufficiency.

## Explicit target decision: separate counterparties and production

[ADR 0002](adr/0002-supplier-producer-production-plot.md) records **Supplier != Producer != ProductionPlot**. A supplier may be an exporter, cooperative or intermediary; a producer may have multiple plots. Implemented (persistence model and internal authenticated API, see [API](API.md); no public routes, see [ADR 0006](adr/0006-production-provenance-plot-revisions.md)): `Producer`, `ProductionPlot` and `PlotRevision`. The case dossier / lot-source persistence model is implemented (see the next section; no API yet, [ADR 0007](adr/0007-case-dossier-lot-attribution.md)). Supplier/producer relationships are **not** implemented. Do not rename GeoSubmission to ProductionPlot, assume one uploaded feature is one plot, or infer Supplier == Producer. Ownership, duplicate identity across producers and any backfill of existing case geometry remain undecided; existing `GeoSubmission` evidence is untouched.

- **Producer:** stable tenant-owned identity (name, optional external reference, optional own country, notes, `IsActive`). Never deleted; deactivated. Descriptive edits overwrite values (no producer history).
- **ProductionPlot:** a stable **producer-scoped plot registration**, not a canonical identity of the physical parcel. Each plot belongs to exactly one producer and `ProducerId` is immutable; a producer or tenure change is a new plot (shared or multi-producer plots are deferred, with no guarantee that adding them is non-breaking). It holds no location data. Never deleted; deactivated.
- **PlotRevision:** immutable material location data (country of production, geometry type Point/Polygon/MultiPolygon, normalized geometry JSON, optional declared `AreaHectares`, note). Exactly one revision is current (`SupersededAtUtc IS NULL`); older revisions stay retrievable and remain valid targets for later references. Only a *material* change creates a revision: geometry, country or declared area. A change that is not material is rejected by `ProvenanceService` with 409 as a no-op; the comparison is `GeoJsonValidationService.IsMaterialChange` — geometry by NetTopologySuite topological equality (ring start, orientation, MultiPolygon component order and a single-component MultiPolygon vs the identical Polygon are not changes), country by exact value, declared area by exact decimal value. The revision note alone is never material.
- **Active semantics (enforced in `ProvenanceService` under row locks, not by the schema):** a plot can only be created for an active producer (decided on the locked producer row); a revision can only be created for an active plot (decided on the locked plot row; the producer's activity is not checked); deactivating a plot does not supersede its current revision.
- **Plot geometry validation:** `GeoJsonValidationService.ValidatePlotGeometry` accepts one bare Point, Polygon or MultiPolygon geometry object (no Feature/FeatureCollection) with the same range/closure/topology checks as case uploads, a 5 MiB size cap, and no vertex limit. **Valid plot geometry means structural validity only**, not EUDR completeness or sufficiency of the geolocation (no 4 ha point/polygon rule, no precision rule). Case uploads (`Validate`) still reject Point.

## Case dossier and lot sources

Implemented as a persistence model only; there is **no API, service or shipment lock yet** (they arrive with the next commit), so nothing can create these rows through the application today. Recorded facts only: this is not applicability, Article 9 completeness, risk assessment or a DDS ([ADR 0007](adr/0007-case-dossier-lot-attribution.md)).

- **CaseDossier:** the stable case-owned anchor representing the case's one physical lot. At most one per case; fully immutable; never deleted. It holds no business facts. Multi-lot cases, lot transfer, split, merge and processing/conversion are unsupported.
- **CaseLot:** an immutable version of the facts about that lot (sequential `Number`, `LotReference`, `Quantity` `numeric(20,6)`, `Unit`, optional note). Exactly one version is current; saving new facts will supersede it. **`LotReference` is a versioned operator business/batch reference, not a permanent lot identity:** it is unique among *current* lots in an organization, a superseded reference may be reused, and the dossier (not the reference) is what ties the physical lot to its case.
- **LotContribution:** one producer's quantity in one lot version, in the lot's unit. No supplier is recorded or inferred; Supplier == Producer is never assumed.
- **ContributionPlot:** attributes a contribution to one plot at one **pinned** `PlotRevision`, so later plot edits cannot rewrite what a lot version used. No per-plot quantity. The database enforces that the revision belongs to the plot, the plot belongs to the producer, and that producer is the contribution's producer (composite FKs). Optional `ProductionStartDate`/`ProductionEndDate`: both or neither, start <= end (API validator rules, not SQL). These dates are factual source data; they are not a completeness or legal-sufficiency claim and are not compared with the EUDR cut-off date or plot revision dates.
- **Quantity semantics (enforced by the API in the next commit):** a contribution is the portion of the lot's own material from that producer in the lot's unit; reconciliation is derived on read by exact decimal equality; same unit only, no conversion, no tolerance, over-allocation rejected, under-allocation storable.
- **Not implemented:** the dossier API, the active/carry-over rules for new attribution, the shipment lock once a dossier exists, export additions, and everything in Slice 5 (completeness, review, snapshots). `GeoSubmission` is never converted into lot sources.

## Responsibility boundary (project rule)

| Actor | Responsibility | Must not be inferred |
| --- | --- | --- |
| Supplier | Provide evidence and attest submission completion | Acceptance or compliance |
| Internal operator/reviewer | Decide requirement acceptance/rejection/waiver | Formal legal verification merely from authentication |
| Provena automation | Format/geometry checks, checklist calculation, operational packaging | Regulatory risk assessment or legality |
| Regulatory/external process | Outside this implementation; optional reference recorded | Successful DDS submission or official approval |

Pending owner decisions include waiver justification/permissions; whether supplier completion must precede readiness; review-decision history, reviewed dossier snapshots and historical catalog snapshots (evidence version rows are retained, but not yet snapshotted or linked to decisions); retention period, disposal and GDPR erasure (the pilot has no erasure path; any exceptional erasure would be a manual out-of-band procedure); whether identical repeated details saves should be deduplicated; whether incomplete/archived exports stay allowed; and how production concepts relate to existing shipments beyond the dossier model above (completeness, applicability and review of lot attribution are undecided). No legal deadline, country-risk list or mandatory regulatory data rule is invented in this foundation.
