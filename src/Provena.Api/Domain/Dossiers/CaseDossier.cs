namespace Provena.Api.Domain;

// Stable case-owned anchor representing the case's one physical lot. Fully immutable after insert and never deleted.
// Holds no business facts: those are versioned as CaseLot rows. At most one dossier per case.
public sealed class CaseDossier : TenantEntity
{
    public Guid EudrCaseId { get; set; }
    public Guid? CreatedByUserId { get; set; }
}

// Immutable version of the facts about the dossier's physical lot. The only allowed update is the one-way SupersededAtUtc retirement; the current version has it null.
// LotReference is a versioned operator business/batch reference (unique among current lots in an organization), not a permanent identity of the physical lot.
public sealed class CaseLot : TenantEntity
{
    public Guid CaseDossierId { get; set; }
    public int Number { get; set; }
    public string LotReference { get; set; } = "";
    public decimal Quantity { get; set; }
    public QuantityUnit Unit { get; set; }
    public string? Note { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset? SupersededAtUtc { get; set; }
}

// One producer's quantity within one lot version. Fully immutable; no supplier is recorded or inferred.
public sealed class LotContribution : TenantEntity
{
    public Guid CaseLotId { get; set; }
    public Guid ProducerId { get; set; }
    public decimal Quantity { get; set; }
    public QuantityUnit Unit { get; set; }
}

// Source attribution of a contribution to one plot at one exact, pinned revision. Fully immutable; carries no quantity.
// ProducerId is a denormalized copy of the contribution's producer, kept so composite foreign keys enforce the producer -> plot -> revision chain.
// Production period rules (both or neither, start <= end) are validated by the API, not by SQL.
public sealed class ContributionPlot : TenantEntity
{
    public Guid LotContributionId { get; set; }
    public Guid ProducerId { get; set; }
    public Guid ProductionPlotId { get; set; }
    public Guid PlotRevisionId { get; set; }
    public DateOnly? ProductionStartDate { get; set; }
    public DateOnly? ProductionEndDate { get; set; }
}
