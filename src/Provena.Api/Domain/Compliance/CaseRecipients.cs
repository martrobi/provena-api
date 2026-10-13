namespace Provena.Api.Domain;

public enum RecipientResolution { RecipientsIdentified, NotApplicable }

// Immutable, versioned statement about who receives the lot. No statement means unresolved (never silently "not applicable").
// The only allowed update is the one-way SupersededAtUtc retirement; at most one current version per case.
// RecipientsIdentified carries 1..50 CaseRecipient rows; NotApplicable carries none and a recorded basis.
public sealed class CaseRecipientStatement : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public RecipientResolution Resolution { get; set; }
    public string? NotApplicableBasis { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}

// One identified recipient of a statement version. Fully immutable and never deleted.
public sealed class CaseRecipient : TenantEntity
{
    public Guid CaseRecipientStatementId { get; set; }
    public string Name { get; set; } = "";
    public string PostalAddress { get; set; } = "";
    public string Email { get; set; } = "";
}
