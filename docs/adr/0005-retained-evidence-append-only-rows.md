# ADR 0005: Retained evidence as append-only rows with a current marker

- Date: 2026-10-13
- Decision status: Accepted
- Implementation status: Implemented (write paths and the internal history read API)
- Decision owner / approval evidence: Owner approval of `docs/plans/2026-10-retained-evidence-foundation.md` (2026-10-13), with owner answers: minimal history read API, export contains current evidence only plus a scope notice, review-decision history deferred, no erasure path in the pilot (recorded as an open legal item).
- Supersedes / superseded by: none.

## Context and evidence

Three operations destroyed evidence: document DELETE removed the row and bytes, geometry replacement overwrote the single row and deleted the old bytes, and supplier details saves overwrote the single submission row (`CaseFileService`, `SupplierRequestService`). Earlier submitted or reviewed evidence was unrecoverable.

## Decision

- `CaseDocument`, `GeoSubmission` and `SupplierSubmission` rows are append-only. Each row is immutable after insert except for one one-way retirement transition: `WithdrawnAtUtc`/`WithdrawnByUserId` (documents, internal withdrawal) or `SupersededAtUtc` (geometry and submissions, replaced by a newer version).
- "Current" means not retired. Exactly one current geometry and one current submission per case is enforced by partial unique indexes (`WHERE "SupersededAtUtc" IS NULL`); many current documents are allowed.
- Workflows, review, export and the public portal read current rows through `Data/RetainedEvidence.cs` `Current()`; there is no global query filter, so history reads never need `IgnoreQueryFilters`.
- Committed evidence bytes are never deleted. `IFileStorage.DeleteAsync` is used only to clean up bytes of an uncommitted upload.
- `AppDbContext.SaveChangesAsync(CancellationToken)` rejects deletes and any modification other than the retirement. This is application-level only; other save overloads, bulk writes and raw SQL bypass it.
- No version numbers and no separate history tables. Version order is `CreatedAtUtc` then `Id`. Legacy rows simply become current.

## Alternatives considered

- **Separate history tables or a generic versioning framework.** Rejected: duplicates schemas and adds machinery for three entities.
- **Global query filter hiding retired rows.** Rejected: history reads would need `IgnoreQueryFilters`, which requires security review.
- **Keep overwrite and rely on audit events.** Rejected: audit text holds no values or bytes.

## Consequences

- Behavioural changes: DELETE withdraws (repeat returns 204, withdrawn documents stay downloadable); `GeoDto.id` and `supplierSubmission.id` change on every replacement/save; audit event `DocumentWithdrawn` replaces new `DocumentDeleted` emissions; export manifest gains `evidenceScope` and `retainedNotIncluded`.
- Retained history is structurally unbounded (every details save creates a row, even identical ones). Accepted at pilot scale; capacity work is deferred.
- The migration Down is refused once any retained version exists.
- Review decisions are still cleared on resubmission and are not linked to versions; reviewed dossier snapshots are deferred.
- There is no erasure/disposal path: retention period and GDPR erasure are an open legal decision.

## Implementation and verification

Source: `Domain/Files/CaseFiles.cs`, `Domain/EudrCases/EudrCase.cs`, `Data/AppDbContext.cs`, `Data/RetainedEvidence.cs`, `Data/Migrations/*RetainedEvidence*`, `Services/CaseFileService.cs`, `SupplierRequestService.cs`, `ExportService.cs`. Tests: `RetainedEvidenceTests`, `MigrationTests`.

## Revisit trigger

Revisit when reviewed dossier snapshots are designed, when retention/erasure is decided, or when retained history growth needs pagination or archival.
