namespace Provena.Api.Domain;

public enum ApplicabilityActivityType { PlacingOnMarketByImport, PlacingOnMarketDomestic, Export }
// Operator is the only supported role; OtherUnsupported records a human assessment that the organization acts in another role (always an unsupported flow).
public enum OperatorRole { Operator, OtherUnsupported }
public enum ActivityDateKind { Planned, Actual }
// RequiresLegalReview is appended last (numeric values are persisted): the Owner determined that applicability cannot currently be resolved without legal/domain review.
// It is none of InScope, OutsideRegulatoryScope or UnsupportedFlow, and never a non-compliance finding.
public enum RegulatoryScope { Pending, InScope, OutsideRegulatoryScope, RequiresLegalReview }
public enum FlowSupport { Pending, Supported, UnsupportedFlow }
public enum ApplicabilityStatus { NotRecorded, ProductChanged, Pending, OutsideRegulatoryScope, UnsupportedFlow, WithinSupportedScope, RequiresLegalReview }

// Immutable, versioned human assessment of whether the case's product and flow are in regulatory scope and inside the pilot's supported envelope.
// The only allowed update is the one-way SupersededAtUtc retirement; at most one current version per case.
// Assessed* are copied by the server from the case's current CaseMasterDataVersion (never from the client or the live Product).
// ProductDescription is the authoritative assessed trade description (distinct from the catalogue Product.Description).
// A record is a recorded assessment, not a legal determination by Provena.
public sealed class CaseApplicability : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public int Number { get; set; }
    public Guid AssessedProductId { get; set; }
    public string AssessedCnCode { get; set; } = "";
    public CommodityType AssessedCommodity { get; set; }
    public string? ProductDescription { get; set; }
    public bool SingleRelevantCommodity { get; set; }
    public ApplicabilityActivityType ActivityType { get; set; }
    public OperatorRole OperatorRole { get; set; }
    public string? OperatorRoleDescription { get; set; }
    public DateOnly? ActivityDate { get; set; }
    public ActivityDateKind ActivityDateKind { get; set; }
    public string? ScopeCitation { get; set; }
    public string? Qualifications { get; set; }
    public string? LegalBasisVersion { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public string? Reasoning { get; set; }
    public RegulatoryScope RegulatoryScope { get; set; }
    public FlowSupport FlowSupport { get; set; }
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}
