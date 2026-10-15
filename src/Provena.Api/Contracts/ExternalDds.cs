namespace Provena.Api.Contracts;

// External DDS Record v1 contracts. No type here carries OrganizationId. The record is a manually recorded fact about a submission made OUTSIDE Provena; it is not an approval,
// EU acceptance, risk clearance or compliance claim. VerificationNumber is access-enabling: it appears only in ExternalDdsRecordDto, which only the dedicated External DDS endpoints return.

// expectedLatestExternalDdsRecordId: null for the first record, otherwise the id of the current record the Owner loaded. replacementReason: required for a replacement, rejected for the first record.
public sealed record CreateExternalDdsRecordInput(Guid? ExpectedLatestExternalDdsRecordId, Guid? ReviewId, string? ReferenceNumber, string? VerificationNumber, DateTimeOffset? SubmittedAtUtc,
    Guid? ReceiptDocumentId, string? ReplacementReason);

public sealed record ExternalDdsRecordDto(Guid RecordId, int Number, Guid ReviewId, string ReferenceNumber, string VerificationNumber, DateTimeOffset SubmittedAtUtc, Guid RecordedByUserId,
    DateTimeOffset RecordedAtUtc, Guid? ReceiptDocumentId, Guid? SupersedesRecordId, string? ReplacementReason, DateTimeOffset? SupersededAtUtc, bool IsCurrent);

// The safe summary a failed close may carry: never the verification number.
public sealed record ExternalDdsSummaryDto(Guid RecordId, Guid ReviewId, string ReferenceNumber, DateTimeOffset SubmittedAtUtc);
