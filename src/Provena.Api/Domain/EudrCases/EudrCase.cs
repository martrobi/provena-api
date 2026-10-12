namespace Provena.Api.Domain;

public enum EudrCaseStatus { Draft, WaitingForSupplier, ReviewNeeded, ReadyForExport, Completed, Archived }

public sealed class EudrCase : TenantEntity
{
    public Guid ShipmentId { get; set; }
    public EudrCaseStatus Status { get; set; }
    public DateTimeOffset? SupplierSubmissionCompletedAtUtc { get; set; }
    public DateTimeOffset? InternalReviewCompletedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? InternalNotes { get; set; }
    public string? ExternalDdsReference { get; set; }
}

public enum RequirementType { SupplierInformation, CountryOfProduction, ProductionPeriod, Geolocation, SupportingDocuments, QuantityConfirmation }

public enum RequirementStatus { Missing, Submitted, Accepted, Rejected, NotRequired }

public sealed class EudrRequirement : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public RequirementType Type { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public bool IsRequired { get; set; } = true;
    public RequirementStatus Status { get; set; }
    public string? ReviewerNote { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
}

public sealed class SupplierSubmission : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public string? CountryOfProductionCode { get; set; }
    public DateOnly? ProductionPeriodFrom { get; set; }
    public DateOnly? ProductionPeriodTo { get; set; }
    public decimal? ConfirmedQuantity { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public Guid? SupplierRequestId { get; set; }
    // One-way retirement marker: set when a later details save supersedes this version. Values are never overwritten.
    public DateTimeOffset? SupersededAtUtc { get; set; }
}
