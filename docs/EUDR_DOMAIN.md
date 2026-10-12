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

The validator supports Polygon/MultiPolygon directly or wrapped in Feature/FeatureCollection. It checks nonempty geometry, finite coordinates, longitude/latitude ranges, closed rings and topology with NetTopologySuite; Feature properties must be object/null. Point geometries are rejected. A third coordinate is allowed and checked for finiteness but topology uses only the first two. FeatureCount counts geometry objects processed, not individual polygons inside a MultiPolygon and not production plots.

Invalid uploads return 400 rather than saving an Invalid GeoSubmission. Valid geometry means structural/topological validity only. It does not prove location authenticity, land ownership, production attribution, deforestation status or regulatory sufficiency.

## Explicit target decision: separate counterparties and production

[ADR 0002](adr/0002-supplier-producer-production-plot.md) records **Supplier != Producer != ProductionPlot**. A supplier may be an exporter, cooperative or intermediary; a producer may have multiple plots. Producer and ProductionPlot entities, stable plot IDs, supplier/producer relationships and shipment/plot allocation are not implemented. Do not rename GeoSubmission to ProductionPlot or assume one uploaded feature is one plot. Cardinalities, ownership, duplicate identity, historical provenance and migration/backfill require a feature plan and owner decisions.

## Responsibility boundary (project rule)

| Actor | Responsibility | Must not be inferred |
| --- | --- | --- |
| Supplier | Provide evidence and attest submission completion | Acceptance or compliance |
| Internal operator/reviewer | Decide requirement acceptance/rejection/waiver | Formal legal verification merely from authentication |
| Provena automation | Format/geometry checks, checklist calculation, operational packaging | Regulatory risk assessment or legality |
| Regulatory/external process | Outside this implementation; optional reference recorded | Successful DDS submission or official approval |

Pending owner decisions include waiver justification/permissions; whether supplier completion must precede readiness; review-decision history, reviewed dossier snapshots and historical catalog snapshots (evidence version rows are retained, but not yet snapshotted or linked to decisions); retention period, disposal and GDPR erasure (the pilot has no erasure path; any exceptional erasure would be a manual out-of-band procedure); whether identical repeated details saves should be deduplicated; whether incomplete/archived exports stay allowed; and how production concepts relate to existing shipments. No legal deadline, country-risk list or mandatory regulatory data rule is invented in this foundation.
