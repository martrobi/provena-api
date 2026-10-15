namespace Provena.Api.Domain;

// A manually recorded external DDS submission fact (External DDS Record v1): Provena records that the Owner submitted the dossier version captured by ReviewId to the EU
// Information System OUTSIDE Provena. It is NOT a regulatory approval, EU acceptance, risk clearance or compliance claim, and Provena makes no network call. The review (immutable) is the
// Provena version associated with the submission; no dossier facts are copied here. Append-only: a correction is a NEW row that supersedes the previous current one (Number is the per-case
// sequence; at most one non-superseded row per case, partial unique index). The only allowed update of an existing row is the one-way SupersededAtUtc null -> value (retirement guard);
// never deleted. ReferenceNumber and VerificationNumber are separate, trimmed, nonblank values. VerificationNumber is access-enabling: it is returned only by the dedicated External DDS
// endpoints and never put into other DTOs, audit text, ProblemDetails or logs. ReceiptDocumentId pins a retained case document (a later withdrawal keeps the link).
public sealed class ExternalDdsRecord : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public Guid ReviewId { get; set; }
    public string ReferenceNumber { get; set; } = "";
    public string VerificationNumber { get; set; } = "";
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public Guid RecordedByUserId { get; set; }
    public Guid? ReceiptDocumentId { get; set; }
    public Guid? SupersedesExternalDdsRecordId { get; set; }
    public string? ReplacementReason { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}
