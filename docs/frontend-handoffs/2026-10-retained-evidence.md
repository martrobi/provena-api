# Retained evidence: withdraw and supersede instead of delete/overwrite

## 1. Summary

Documents, geometry uploads and supplier details are now retained instead of being deleted or overwritten. Internal document DELETE **withdraws** the document, a new geometry upload **supersedes** the previous one, and every supplier details save creates a new version. Every existing endpoint still returns only **current** evidence, so most clients see the same shapes with a few identity and value changes listed below. No history read endpoint exists yet (planned as a follow-up commit; this handoff will be updated then).

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

No route is added or removed. Public (token) route shapes are unchanged.

## 3. Request/query contract

Unchanged. DELETE still takes no body (no withdrawal reason).

## 4. Response contract

- `DocumentDto` gains `withdrawnAtUtc` (`null` in current-only lists).
- `GeoDto` gains `supersededAtUtc` (always `null` on current responses).
- `supplierSubmission` (inside `CaseDetails` and the export manifest) gains `supersededAtUtc` (`null`) and `supplierRequestId` (`null` for legacy rows), and carries internal-only fields as before.
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

- Retained history grows without bound (each details save adds a row, even if identical). Not visible to clients yet.
- Review decisions are still cleared when evidence changes after review; only the evidence versions are retained.
- There is no erasure path in the pilot; retention is an open legal decision. Do not promise users that withdrawn files are deleted.
- Client compatibility is not tested.

## 9. Out of scope

History read endpoints and the geometry-version file download (separate follow-up commit), withdrawal reasons, document replacement lineage, supplier-side withdrawal, reviewed dossier snapshots, review-decision history, retention/erasure.

## 10. Backend verification performed

See the final report for the change: `pwsh -File scripts/verify.ps1` plus focused `RetainedEvidenceTests` and `MigrationTests`.
