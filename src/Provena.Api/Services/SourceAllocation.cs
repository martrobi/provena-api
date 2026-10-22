using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// One producer contribution with the allocation of each of its plot links (null = not recorded). Order of the allocations is irrelevant.
public sealed record AllocationContribution(Guid ProducerId, decimal Quantity, IReadOnlyList<decimal?> PlotAllocations);

// Pure Source Quantity Allocation v1 (ADR 0007 amendment, completeness policy v3): how much of the lot is attributed to concrete production plots. Derived on every read, export and
// review evaluation; never stored, never fingerprinted (its inputs, ContributionPlot.AllocatedQuantity and the contribution/lot quantities, are) and never manually overridden. Exact
// decimal equality in the lot's unit: no unit conversion, no tolerance, no percentages. It answers a different question from Quantity Reconciliation (lot -> sources, not
// shipment/confirmed/lot), so it is a separate layer and never a check of QuantityReconciliation. It is quantity attribution only: not a legal net-mass, yield or compliance finding.
// Allocation is optional: NotRecorded (no plot carries a value, every legacy lot) is not a defect. Once any value exists the allocation must be complete before it reconciles.
// Precedence per contribution and for the lot: OverAllocated, then MissingData, then UnderAllocated, then Matched. Matched needs every contribution fully allocated to its plots
// and the plot total to equal the lot quantity (so contributions below the lot are UnderAllocated even when each contribution is fully allocated).
public static class SourceAllocation
{
    public const string ItemCode = "SOURCE_ALLOCATION_RECONCILED";
    public const string Scope = "Derived attribution of the lot quantity to the recorded production plots (exact decimal equality in the lot unit; no conversion, tolerance or percentage). Quantity attribution only; not a legal net-mass, yield or compliance determination.";

    public static SourceAllocationDto Derive(decimal lotQuantity, QuantityUnit unit, IEnumerable<AllocationContribution> contributions)
    {
        var rows = contributions.ToList();
        var recorded = rows.Any(c => c.PlotAllocations.Any(a => a != null));
        var total = rows.Sum(c => c.PlotAllocations.Sum(a => a ?? 0m));
        var dtos = rows.Select(c =>
        {
            var allocated = c.PlotAllocations.Sum(a => a ?? 0m);
            var status = !recorded ? SourceAllocationStatus.NotRecorded
                : allocated > c.Quantity ? SourceAllocationStatus.OverAllocated
                : c.PlotAllocations.Count == 0 || c.PlotAllocations.Any(a => a == null) ? SourceAllocationStatus.MissingData
                : allocated < c.Quantity ? SourceAllocationStatus.UnderAllocated : SourceAllocationStatus.Matched;
            return new SourceAllocationContributionDto(c.ProducerId, c.Quantity, allocated, c.Quantity - allocated, status);
        }).OrderBy(c => c.ProducerId).ToList();
        var lot = !recorded ? SourceAllocationStatus.NotRecorded
            : total > lotQuantity || dtos.Any(c => c.Status == SourceAllocationStatus.OverAllocated) ? SourceAllocationStatus.OverAllocated
            : dtos.Any(c => c.Status == SourceAllocationStatus.MissingData) ? SourceAllocationStatus.MissingData
            : total < lotQuantity || dtos.Any(c => c.Status == SourceAllocationStatus.UnderAllocated) ? SourceAllocationStatus.UnderAllocated : SourceAllocationStatus.Matched;
        return new(lot, lotQuantity, unit, total, lotQuantity - total, dtos);
    }

    // null when there is no lot (nothing to attribute).
    public static SourceAllocationDto? FromFacts(DossierFacts f) => f.Lot == null ? null :
        Derive(f.Lot.Quantity, f.Lot.Unit, f.Contributions.Select(c => new AllocationContribution(c.Contribution.ProducerId, c.Contribution.Quantity, c.Links.Select(l => l.Link.AllocatedQuantity).ToList())));

    public static SourceAllocationDto FromRows(CaseLot lot, IEnumerable<LotContribution> contributions, IReadOnlyCollection<ContributionPlot> links) =>
        Derive(lot.Quantity, lot.Unit, contributions.Select(c => new AllocationContribution(c.ProducerId, c.Quantity, links.Where(l => l.LotContributionId == c.Id).Select(l => l.AllocatedQuantity).ToList())));

    // The retained review snapshot only: never a live producer, plot, revision or lot row. A schema v1 snapshot carries no allocation, so it is NotRecorded.
    public static SourceAllocationDto? FromSnapshot(SnapshotLotV2? lot) => lot == null ? null :
        Derive(lot.Quantity, lot.Unit, lot.Contributions.Select(c => new AllocationContribution(c.ProducerId, c.Quantity, c.Plots.Select(p => p.AllocatedQuantity).ToList())));

    public static CompletenessItemStatus ItemStatus(SourceAllocationStatus status) => status switch
    {
        SourceAllocationStatus.NotRecorded => CompletenessItemStatus.NotApplicable,
        SourceAllocationStatus.Matched => CompletenessItemStatus.Satisfied,
        SourceAllocationStatus.MissingData => CompletenessItemStatus.Missing,
        _ => CompletenessItemStatus.Invalid,
    };
}
