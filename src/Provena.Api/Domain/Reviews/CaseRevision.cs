namespace Provena.Api.Domain;

public enum ReviewDecision { AcceptedAsCompleteUnderPolicy, ConfirmedOutsideRegulatoryScope, ConfirmedUnsupportedFlow, ReturnedForCorrection }

// Immutable record of exactly what one accountable review relied on (ADR 0008, hybrid snapshot). The immutable rows are pinned by case-consistent
// composite Restrict FKs; the values that can change later (labels, catalogue context) are copied into the typed SnapshotJson (CaseRevisionSnapshotV1).
// Number is the per-case sequence and the only definition of "latest" (never a timestamp). Fingerprint/SectionHashes identify the fact-bearing content.
// CaseDossierId and CaseLotId are both null or both set (database CHECK CK_CaseRevisions_DossierLotPair); that the lot is the CURRENT lot at review time
// is a ReviewService invariant. Fully immutable and never deleted (application-level guard).
public sealed class CaseRevision : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public Guid? CaseDossierId { get; set; }
    public Guid? CaseLotId { get; set; }
    public Guid? CaseApplicabilityId { get; set; }
    public Guid? CaseRecipientStatementId { get; set; }
    public Guid? CaseMasterDataVersionId { get; set; }
    public Guid? GeoSubmissionId { get; set; }
    public Guid? SupplierSubmissionId { get; set; }
    public string PolicyId { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public int SnapshotSchemaVersion { get; set; }
    public int FingerprintVersion { get; set; }
    public string Fingerprint { get; set; } = "";
    public string SectionHashes { get; set; } = "{}";
    public string SnapshotJson { get; set; } = "{}";
}

// A current document that was part of the reviewed dossier. Pinned by id, so a document withdrawn later is still the exact reviewed one.
public sealed class CaseRevisionDocument : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid CaseRevisionId { get; set; }
    public Guid CaseDocumentId { get; set; }
}

// A non-withdrawn evidence link that was part of the reviewed dossier.
public sealed class CaseRevisionEvidenceLink : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid CaseRevisionId { get; set; }
    public Guid CaseEvidenceLinkId { get; set; }
}

// The human decision made on exactly one CaseRevision (one-to-one). Fully immutable; a new decision is a new revision and record.
// "Accepted" only means: accepted as complete under the versioned Provena pilot completeness policy. It is not a legal-compliance determination.
public sealed class ReviewRecord : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid CaseRevisionId { get; set; }
    public ReviewDecision Decision { get; set; }
    public string Rationale { get; set; } = "";
    public Guid ReviewerUserId { get; set; }
    public DateTimeOffset ReviewedAtUtc { get; set; }
    public string PolicyVersion { get; set; } = "";
}
