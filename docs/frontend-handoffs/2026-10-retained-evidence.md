# Retained evidence: withdraw and supersede instead of delete/overwrite

## 1. Summary

Documents, geometry uploads and supplier details are now retained instead of being deleted or overwritten. Internal document DELETE **withdraws** the document, a new geometry upload **supersedes** the previous one, and every supplier details save creates a new version. Every existing endpoint still returns only **current** evidence, so most clients see the same shapes with a few identity and value changes listed below. Two new internal read-only routes expose the retained history: `GET /api/eudr-cases/{caseId}/evidence-history` and `GET /api/eudr-cases/{caseId}/geolocation/{geolocationId}/file`.

## 2. Changed endpoints or behaviour

| Endpoint / output | Change | Kind |
| --- | --- | --- |
| `DELETE /api/eudr-cases/{caseId}/documents/{documentId}` | Withdraws the document (row and file retained). A repeat call on a withdrawn document returns 204 as a no-op (previously 404). | Behavioural |
| `GET /api/eudr-cases/{caseId}/documents/{documentId}` | Also downloads a withdrawn document (previously 404 after delete). | Additive |
| `GET /api/eudr-cases/{caseId}/documents`, `CaseDetails.documents` | Current documents only (same as what clients observed after a delete). | None |
| `POST /api/eudr-cases/{caseId}/geolocation`, `POST /public/supplier-requests/{token}/geolocation` | Creates a new geometry; the previous one is retained as superseded. | Behavioural |
| `GET /api/eudr-cases/{caseId}/geolocation`, `CaseDetails.geolocation` | Returns the current geometry; its `id` **changes after every replacement** (previously stable). | **Potentially breaking** |
| `PUT /public/supplier-requests/{token}/details` | Creates a new supplier-details version (also for identical values). | Behavioural |
| `CaseDetails.supplierSubmission` | Current version; its `id` **changes on every save** (previously stable). | **Potentially breaking** |
| Audit events | New events use type `DocumentWithdrawn` (description "Document {id} withdrawn from the current dossier; file retained.") instead of `DocumentDeleted`. Historic `DocumentDeleted` events still appear. `SupplierDetailsUpdated` descriptions now include the submission id. | Value-set change |
| Export ZIP `manifest.json` | Current evidence only (unchanged folder layout). Adds `evidenceScope` and `retainedNotIncluded`. | Additive |
| **New** `GET /api/eudr-cases/{caseId}/evidence-history` | Retained versions of documents, geometries and supplier-details, newest first. | Additive route |
| **New** `GET /api/eudr-cases/{caseId}/geolocation/{geolocationId}/file` | Original bytes of any retained geometry version (`application/geo+json` attachment). | Additive route |

Two routes are added (above); none are removed. Public (token) routes are unchanged and have no history equivalent.

## 3. Request/query contract

Unchanged. DELETE still takes no body (no withdrawal reason).

## 4. Response contract

- `DocumentDto` gains `withdrawnAtUtc` (`null` in current-only lists).
- `GeoDto` gains `supersededAtUtc` (always `null` on current responses).
- `supplierSubmission` (inside `CaseDetails` and the export manifest) gains `supersededAtUtc` (`null`) and `supplierRequestId` (`null` for legacy rows), and carries internal-only fields as before.
- `GET /api/eudr-cases/{caseId}/evidence-history` (authenticated, internal only), newest first, unpaginated (pilot-scale assumption):

```json
{
  "documents": [ { "id": "…", "originalFileName": "two.txt", "uploadedAtUtc": "…", "withdrawnAtUtc": null } ],
  "geolocations": [ { "id": "…", "originalFileName": "v2.geojson", "uploadedAtUtc": "…", "supersededAtUtc": null } ],
  "supplierSubmissions": [ { "id": "…", "countryOfProductionCode": "BR", "productionPeriodFrom": "2026-01-01", "productionPeriodTo": "2026-06-30", "confirmedQuantity": 100, "notes": "…", "submittedAtUtc": "…", "supersededAtUtc": null, "supplierRequestId": "…", "createdAtUtc": "…" } ]
}
```

  (Document and geometry items abbreviated; they use the existing `DocumentDto`/`GeoDto` shapes.) The current version of each kind is the one whose `withdrawnAtUtc`/`supersededAtUtc` is `null`. Version order is `createdAtUtc`, then `id`; there are no version numbers, and legacy rows may have a `null` `supplierRequestId`.
- `GET /api/eudr-cases/{caseId}/geolocation/{geolocationId}/file`: the original uploaded bytes of any retained geometry version of that case, `Content-Type: application/geo+json`, attachment disposition with the original file name, `X-Content-Type-Options: nosniff`. Withdrawn document bytes remain available through the existing `GET /documents/{documentId}`.
- Export manifest:

```json
{
  "evidenceScope": "Current working evidence at export time. This is not a reviewed dossier snapshot; withdrawn or superseded versions are retained in Provena and are not included.",
  "retainedNotIncluded": { "withdrawnDocuments": 1, "supersededGeolocations": 2, "supersededSupplierSubmissions": 0 }
}
```

## 5. Error/status behaviour

- DELETE on an unknown or another organization's document: 404 (unchanged). On a Completed/Archived case: 409 (unchanged). On an already-withdrawn document: 204.
- Download of a withdrawn document: 200 with the original bytes.
- `evidence-history` and the geometry version file: 404 for an unknown case or geometry id, another organization's resources, or a geometry id belonging to a different case; 401 without authentication.

## 6. Breaking-change notes

- `geolocation.id` and `supplierSubmission.id` are no longer stable across replacements/saves. A client that caches or keys UI state on them must treat a changed id as "the current version was replaced".
- A client that treated a repeat DELETE (404) as "already gone" now gets 204; both mean the document is not current.
- Clients mapping audit `eventType` to labels must handle `DocumentWithdrawn`.

## 7. Required frontend adaptations (contract level)

- Do not key long-lived state on geometry or supplier-details ids; refetch after upload/save.
- Add a label for `DocumentWithdrawn` (keep `DocumentDeleted` for old events).
- Optionally wording: "Remove" on a document now means "withdraw from the current dossier"; the file is retained and can still be downloaded by id.
- The new nullable fields may be ignored; they are null on every current-only response.

## 8. Compatibility risks

- Retained history grows without bound (each details save adds a row, even if identical), and `evidence-history` is unpaginated as an approved pilot-scale assumption. It is not a permanent guarantee; pagination may be added later as a contract change, so clients should not assume small lists.
- Review decisions are still cleared when evidence changes after review; only the evidence versions are retained.
- There is no erasure path in the pilot; retention is an open legal decision. Do not promise users that withdrawn files are deleted.
- Client compatibility is not tested.

## 9. Out of scope

Withdrawal reasons, document replacement lineage, supplier-side withdrawal, reviewed dossier snapshots, review-decision history, retention/erasure.

## 10. Backend verification performed

Release build, unit tests and integration tests via `scripts/verify`, including `RetainedEvidenceTests` (history ordering, version file bytes/headers, tenant and case isolation, 401/404), `MigrationTests` and the endpoint inventory/OpenAPI contract tests. Client compatibility is not tested.
