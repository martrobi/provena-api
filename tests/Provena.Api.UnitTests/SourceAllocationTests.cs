using System.Text.Json;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Source Quantity Allocation v1: the pure derivation, completeness policy v3, the write rules, fingerprint v2 and snapshot schema v2.
public sealed class SourceAllocationTests
{
    private static Guid P(int n) => new($"00000000-0000-0000-0000-{n:D12}");
    private static AllocationContribution C(int producer, decimal quantity, params decimal?[] plots) => new(P(producer), quantity, plots);
    private static SourceAllocationDto Derive(decimal lot, params AllocationContribution[] contributions) => SourceAllocation.Derive(lot, QuantityUnit.Kg, contributions);

    // ---- pure derivation ----------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Nothing_allocated_anywhere_is_not_recorded_whatever_the_contributions_look_like()
    {
        var result = Derive(10m, C(1, 6m, null, null), C(2, 4m));
        Assert.Equal(SourceAllocationStatus.NotRecorded, result.Status);
        Assert.Equal(0m, result.AllocatedQuantity); Assert.Equal(10m, result.UnallocatedQuantity);
        Assert.All(result.Contributions, c => Assert.Equal(SourceAllocationStatus.NotRecorded, c.Status));
        Assert.Equal(SourceAllocationStatus.NotRecorded, Derive(10m).Status);
    }

    [Fact]
    public void One_producer_one_plot_matches_by_exact_decimal_equality()
    {
        var result = Derive(10m, C(1, 10m, 10.000000m));
        Assert.Equal(SourceAllocationStatus.Matched, result.Status); Assert.Equal(0m, result.UnallocatedQuantity);
        Assert.Equal(SourceAllocationStatus.Matched, Derive(1.5m, C(1, 1.5m, 1.500000m)).Status); // 1.5 == 1.500000
        Assert.Equal(SourceAllocationStatus.UnderAllocated, Derive(10m, C(1, 10m, 9.999999m)).Status); // no tolerance
        Assert.Equal(SourceAllocationStatus.OverAllocated, Derive(10m, C(1, 10m, 10.000001m)).Status);
    }

    [Fact]
    public void One_producer_with_several_plots_and_several_producers_sum_exactly()
    {
        Assert.Equal(SourceAllocationStatus.Matched, Derive(10m, C(1, 10m, 4m, 3.5m, 2.5m)).Status);
        var many = Derive(10m, C(1, 6m, 2m, 4m), C(2, 4m, 1.5m, 2.5m));
        Assert.Equal(SourceAllocationStatus.Matched, many.Status); Assert.Equal(10m, many.AllocatedQuantity);
        Assert.Equal([6m, 4m], many.Contributions.OrderBy(c => c.ProducerId).Select(c => c.AllocatedQuantity));
    }

    [Fact]
    public void A_contribution_without_plots_or_with_an_unallocated_plot_is_missing_data_once_allocation_is_recorded()
    {
        var noPlots = Derive(10m, C(1, 6m, 6m), C(2, 4m));
        Assert.Equal(SourceAllocationStatus.MissingData, noPlots.Status);
        Assert.Equal(SourceAllocationStatus.MissingData, noPlots.Contributions.Single(c => c.ProducerId == P(2)).Status);
        Assert.Equal(SourceAllocationStatus.MissingData, Derive(10m, C(1, 10m, 5m, null)).Status); // defensive: the write path forbids a partial set
    }

    [Fact]
    public void Under_allocation_is_a_draft_state_and_contributions_below_the_lot_are_still_under_allocated()
    {
        var under = Derive(10m, C(1, 10m, 4m));
        Assert.Equal(SourceAllocationStatus.UnderAllocated, under.Status); Assert.Equal(6m, under.UnallocatedQuantity);
        Assert.Equal(6m, Assert.Single(under.Contributions).UnallocatedQuantity);
        // Each contribution is fully allocated to its plots, yet the contributions only cover 8 of the 10 kg of the lot.
        var belowLot = Derive(10m, C(1, 5m, 5m), C(2, 3m, 3m));
        Assert.Equal(SourceAllocationStatus.UnderAllocated, belowLot.Status); Assert.Equal(2m, belowLot.UnallocatedQuantity);
        Assert.All(belowLot.Contributions, c => Assert.Equal(SourceAllocationStatus.Matched, c.Status));
    }

    [Fact]
    public void Over_allocation_is_derived_defensively_and_wins_over_missing_data_and_under_allocation()
    {
        var over = Derive(10m, C(1, 6m, 7m), C(2, 4m), C(3, 4m, 1m));
        Assert.Equal(SourceAllocationStatus.OverAllocated, over.Status);
        Assert.Equal(SourceAllocationStatus.OverAllocated, Derive(10m, C(1, 10m, 6m), C(2, 5m, 5m)).Status); // plot total above the lot
        Assert.Equal(SourceAllocationStatus.MissingData, Derive(10m, C(1, 5m, 2m), C(2, 5m)).Status); // missing wins over under
    }

    [Fact]
    public void Derivation_from_facts_reads_the_pinned_links_and_has_no_result_without_a_lot()
    {
        var facts = CompletenessTests.Complete();
        Assert.Equal(SourceAllocationStatus.NotRecorded, SourceAllocation.FromFacts(facts)!.Status);
        facts.Contributions[0].Links[0].Link.AllocatedQuantity = 10m;
        Assert.Equal(SourceAllocationStatus.Matched, SourceAllocation.FromFacts(facts)!.Status);
        Assert.Null(SourceAllocation.FromFacts(facts with { Lot = null, Dossier = null, Contributions = [] }));
    }

    // ---- completeness policy v3 -----------------------------------------------------------------------------------------------------------
    private static CompletenessItemDto Item(DossierFacts f) => CompletenessEvaluator.Evaluate(f).Items.Single(i => i.Code == "SOURCE_ALLOCATION_RECONCILED");
    private static DossierFacts Allocated(decimal? value) { var f = CompletenessTests.Complete(); f.Contributions[0].Links[0].Link.AllocatedQuantity = value; return f; }

    [Fact]
    public void Policy_v3_maps_the_allocation_status_to_a_quantity_item_on_the_lot()
    {
        Assert.Equal("3", CompletenessPolicy.Version);
        var notRecorded = Item(Allocated(null));
        Assert.Equal(CompletenessItemStatus.NotApplicable, notRecorded.Status); Assert.Equal(CompletenessGroup.Quantity, notRecorded.Group); Assert.Equal("CaseLot", notRecorded.SubjectType);
        Assert.Equal(CompletenessItemStatus.Satisfied, Item(Allocated(10m)).Status);
        Assert.Equal(CompletenessItemStatus.Invalid, Item(Allocated(4m)).Status);
        Assert.Equal(CompletenessItemStatus.Invalid, Item(Allocated(11m)).Status);
        var missing = Allocated(10m); var second = new LotContribution { Id = P(900), ProducerId = P(901), Quantity = 1m, Unit = QuantityUnit.Kg };
        missing = missing with { Contributions = [missing.Contributions[0], new(second, [])] };
        Assert.Equal(CompletenessItemStatus.Missing, Item(missing).Status);
        var noLot = CompletenessTests.Complete() with { Lot = null, Dossier = null, Contributions = [] };
        var none = Item(noLot); Assert.Equal(CompletenessItemStatus.NotApplicable, none.Status); Assert.Null(none.SubjectId);
    }

    [Fact]
    public void A_recorded_allocation_must_reconcile_before_acceptance_but_not_recorded_never_blocks()
    {
        Assert.Null(ReviewPreconditions.Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, CompletenessEvaluator.Evaluate(Allocated(null)), Allocated(null)));
        Assert.Null(ReviewPreconditions.Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, CompletenessEvaluator.Evaluate(Allocated(10m)), Allocated(10m)));
        var under = Allocated(4m);
        var failure = ReviewPreconditions.Check(ReviewDecision.AcceptedAsCompleteUnderPolicy, CompletenessEvaluator.Evaluate(under), under)!;
        Assert.Contains("SOURCE_ALLOCATION_RECONCILED", failure.FailingItems.Select(i => i.Code));
        Assert.Null(ReviewPreconditions.Check(ReviewDecision.ReturnedForCorrection, CompletenessEvaluator.Evaluate(under), under)); // no extra semantics for the other decisions
        Assert.Equal(DataCoverage.Incomplete, CompletenessEvaluator.Evaluate(under).DataCoverage);
    }

    // ---- write rules ----------------------------------------------------------------------------------------------------------------------
    private static CaseLotInput Lot(params decimal?[] plots) => new("LOT", 10m, QuantityUnit.Kg, null,
        [new(P(1), 10m, QuantityUnit.Kg, plots.Select((a, i) => new ContributionPlotInput(P(100 + i), P(200 + i), null, null, a)).ToList())], null);
    private static readonly CaseLotValidator Validator = new();

    [Theory]
    [InlineData(5.0)] [InlineData(0.000001)] [InlineData(10)]
    public void Valid_allocated_quantities_are_accepted(double value) => Assert.True(Validator.Validate(Lot((decimal)value)).IsValid);

    [Fact]
    public void Zero_negative_oversized_and_over_precise_allocations_are_invalid()
    {
        Assert.False(Validator.Validate(Lot(0m)).IsValid); Assert.False(Validator.Validate(Lot(-1m)).IsValid);
        Assert.False(Validator.Validate(Lot(100000000000000m)).IsValid); Assert.False(Validator.Validate(Lot(1.0000001m)).IsValid);
        Assert.True(Validator.Validate(Lot((decimal?)null)).IsValid); // not recorded
    }

    [Fact]
    public void A_partial_allocation_set_and_a_clear_combined_with_values_are_invalid()
    {
        Assert.False(Validator.Validate(Lot(5m, null)).IsValid); Assert.False(Validator.Validate(Lot(null, 5m)).IsValid);
        Assert.True(Validator.Validate(Lot(null, null)).IsValid); Assert.True(Validator.Validate(Lot(5m, 5m)).IsValid);
        Assert.False(Validator.Validate(Lot(5m) with { ClearSourceAllocations = true }).IsValid);
        Assert.True(Validator.Validate(Lot((decimal?)null) with { ClearSourceAllocations = true }).IsValid); // the state-dependent rules are the service's
    }

    [Fact]
    public void Plot_allocations_may_not_exceed_their_contribution_but_may_fall_short()
    {
        CaseDossierService.ReconcileAllocations(Lot(4m, 6m)); CaseDossierService.ReconcileAllocations(Lot(4m, 5m)); CaseDossierService.ReconcileAllocations(Lot((decimal?)null));
        var ex = Assert.Throws<ApiException>(() => CaseDossierService.ReconcileAllocations(Lot(4m, 6.000001m)));
        Assert.Equal(400, ex.Status); Assert.Contains("PlotAllocationExceedsContribution", JsonSerializer.Serialize(ex.Errors));
    }

    private static List<ContributionPlot> Stored(params decimal?[] values) => values.Select(v => new ContributionPlot { AllocatedQuantity = v }).ToList();

    [Fact]
    public void Recorded_allocations_are_never_cleared_or_kept_by_omission()
    {
        var recorded = Stored(10m); var none = Stored((decimal?)null);
        CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot((decimal?)null), none);                                              // nothing recorded, nothing supplied
        CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot(5m), none);                                                // first allocation write
        CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot(4m), recorded);                                            // replaced by new values
        CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot((decimal?)null) with { ClearSourceAllocations = true }, recorded);   // explicit clear
        var omitted = Assert.Throws<ApiException>(() => CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot((decimal?)null), recorded));
        Assert.Equal(400, omitted.Status); Assert.Contains("SourceAllocationsNotSupplied", JsonSerializer.Serialize(omitted.Errors));
        var pointless = Assert.Throws<ApiException>(() => CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot((decimal?)null) with { ClearSourceAllocations = true }, none));
        Assert.Equal(400, pointless.Status); Assert.Contains("ClearSourceAllocationsNotApplicable", JsonSerializer.Serialize(pointless.Errors));
        Assert.Throws<ApiException>(() => CaseDossierService.EnsureAllocationsNotSilentlyChanged(Lot((decimal?)null) with { ClearSourceAllocations = true }, [])); // no lot at all
    }

    [Fact]
    public void An_identical_resubmission_compares_allocations_by_value_and_null_is_not_a_value()
    {
        var lot = new CaseLot { Id = P(7), LotReference = "LOT", Quantity = 10m, Unit = QuantityUnit.Kg };
        var contribution = new LotContribution { Id = P(8), ProducerId = P(1), Quantity = 10m, Unit = QuantityUnit.Kg };
        ContributionPlot Link(decimal? a) => new() { LotContributionId = contribution.Id, ProducerId = P(1), ProductionPlotId = P(100), PlotRevisionId = P(200), AllocatedQuantity = a };
        Assert.True(CaseDossierService.IsIdentical(Lot(5.5m), lot, [contribution], [Link(5.500000m)]));
        Assert.False(CaseDossierService.IsIdentical(Lot(5.5m), lot, [contribution], [Link(5.4m)]));
        Assert.False(CaseDossierService.IsIdentical(Lot(5.5m), lot, [contribution], [Link(null)]));
        Assert.False(CaseDossierService.IsIdentical(Lot((decimal?)null), lot, [contribution], [Link(5.5m)]));
        Assert.True(CaseDossierService.IsIdentical(Lot((decimal?)null), lot, [contribution], [Link(null)]));
    }

    // ---- fingerprint v2 -------------------------------------------------------------------------------------------------------------------
    [Fact]
    public void Fingerprint_v1_ignores_allocations_and_v2_covers_them_in_the_lot_section_only()
    {
        var plain = CompletenessTests.Complete(); var withValue = Allocated(10m); var other = Allocated(9m);
        Assert.Equal(DossierFingerprint.Compute(plain, 1).Fingerprint, DossierFingerprint.Compute(withValue, 1).Fingerprint); // frozen canonical form
        var a = DossierFingerprint.Compute(withValue, 2); var b = DossierFingerprint.Compute(other, 2); var c = DossierFingerprint.Compute(plain, 2);
        Assert.Equal(2, a.Version); Assert.NotEqual(a.Fingerprint, b.Fingerprint); Assert.NotEqual(a.Fingerprint, c.Fingerprint);
        Assert.NotEqual(a.SectionHashes["Lot"], b.SectionHashes["Lot"]);
        foreach (var section in DossierFingerprint.Sections.Where(s => s != "Lot")) Assert.Equal(a.SectionHashes[section], b.SectionHashes[section]);
        Assert.Equal(a.Fingerprint, DossierFingerprint.Compute(Allocated(10.000000m), 2).Fingerprint); // canonical decimal: 10 == 10.000000
        Assert.Equal(DossierFingerprint.Compute(plain, 1).SectionHashes["Shipment"], c.SectionHashes["Shipment"]);
    }

    [Fact]
    public void A_version_1_review_goes_stale_on_an_allocation_change_through_the_new_lot_version_and_a_version_2_review_through_the_value()
    {
        (CaseRevision, ReviewRecord) Review(DossierFacts f, int version)
        {
            var fp = DossierFingerprint.Compute(f, version);
            return (new CaseRevision { Id = P(500), Number = 1, PolicyId = CompletenessPolicy.Id, PolicyVersion = "2", FingerprintVersion = version, Fingerprint = fp.Fingerprint, SectionHashes = ReviewSnapshot.SerializeHashes(fp.SectionHashes) },
                new ReviewRecord { Id = P(501), CaseRevisionId = P(500), Decision = ReviewDecision.AcceptedAsCompleteUnderPolicy, ReviewedAtUtc = DateTimeOffset.UtcNow, PolicyVersion = "2" });
        }
        var (r1, rec1) = Review(CompletenessTests.Complete(), 1);
        var changed = Allocated(10m); changed.Lot!.Id = P(777); changed.Lot.Number = 2; // an allocation change writes a new CaseLot version
        var stale = ReviewFreshnessService.Evaluate(r1, rec1, changed);
        Assert.Equal(ReviewFreshness.Stale, stale.Freshness); Assert.Equal(["Lot"], stale.Latest.ChangedAreas);

        var (r2, rec2) = Review(Allocated(10m), 2);
        Assert.Equal(ReviewFreshness.Current, ReviewFreshnessService.Evaluate(r2, rec2, Allocated(10m)).Freshness);
        var value = ReviewFreshnessService.Evaluate(r2, rec2, Allocated(9m)); Assert.Equal(ReviewFreshness.Stale, value.Freshness); Assert.Equal(["Lot"], value.Latest.ChangedAreas);
    }

    private static JsonElement PlotJson(string snapshotJson) => JsonDocument.Parse(snapshotJson).RootElement.GetProperty("facts").GetProperty("lot").GetProperty("contributions")[0].GetProperty("plots")[0].Clone();

    // ---- snapshot schema v2 ---------------------------------------------------------------------------------------------------------------
    [Fact]
    public void New_snapshots_are_schema_v2_and_retain_the_allocation_that_the_derivation_reads_back()
    {
        Assert.Equal(2, ReviewSnapshot.SchemaVersion); Assert.Equal([1, 2], ReviewSnapshot.SupportedVersions);
        var facts = Allocated(10m);
        var snapshot = ReviewSnapshot.Build(facts, CompletenessEvaluator.Evaluate(facts), [], []);
        var json = ReviewSnapshot.Serialize(snapshot);
        using (var doc = JsonDocument.Parse(json)) Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(10m, PlotJson(json).GetProperty("allocatedQuantity").GetDecimal());
        var stored = ReviewSnapshot.Deserialize(json, 2);
        Assert.IsType<CaseRevisionSnapshotV2>(stored); Assert.Equal(json, ReviewSnapshot.Serialize(stored));
        Assert.Equal(10m, Assert.Single(Assert.Single(stored.LotView!.Contributions).Plots).AllocatedQuantity);
        Assert.Equal(SourceAllocationStatus.Matched, SourceAllocation.FromSnapshot(stored.LotView)!.Status);

        var under = Allocated(4m); var underStored = ReviewSnapshot.Deserialize(ReviewSnapshot.Serialize(ReviewSnapshot.Build(under, CompletenessEvaluator.Evaluate(under), [], [])), 2);
        Assert.Equal(SourceAllocationStatus.UnderAllocated, SourceAllocation.FromSnapshot(underStored.LotView)!.Status);
        var none = ReviewSnapshot.Deserialize(ReviewSnapshot.Serialize(ReviewSnapshot.Build(Allocated(null), CompletenessEvaluator.Evaluate(Allocated(null)), [], [])), 2);
        Assert.Equal(SourceAllocationStatus.NotRecorded, SourceAllocation.FromSnapshot(none.LotView)!.Status);
        Assert.Null(SourceAllocation.FromSnapshot(null));
    }

    [Fact]
    public void A_stored_schema_v1_snapshot_stays_readable_in_its_own_shape_and_reads_as_not_recorded()
    {
        var plot = new SnapshotPlotLinkV1(P(100), P(200), 1, "BR", PlotGeometryType.Polygon, null, "h", null, null);
        var lot = new SnapshotLotV1(P(6), P(7), 1, "LOT", 10m, QuantityUnit.Kg, null, new(10m, 0m, QuantityReconciliationStatus.Reconciled), [new(P(1), 10m, QuantityUnit.Kg, [plot])]);
        var facts = CompletenessTests.Complete();
        var v2 = ReviewSnapshot.Build(facts, CompletenessEvaluator.Evaluate(facts), [], []);
        var v1 = new CaseRevisionSnapshotV1(1, new SnapshotFactsV1(v2.Facts.Shipment, v2.Facts.MasterData, v2.Facts.Applicability, v2.Facts.Recipients, lot, v2.Facts.Evidence, v2.Facts.Requirements), v2.Context, v2.Completeness);
        var json = ReviewSnapshot.Serialize(v1);
        Assert.False(PlotJson(json).TryGetProperty("allocatedQuantity", out _)); // the v1 plot contract is untouched
        var stored = ReviewSnapshot.Deserialize(json, 1);
        Assert.IsType<CaseRevisionSnapshotV1>(stored); Assert.Equal(json, ReviewSnapshot.Serialize(stored));
        Assert.Null(Assert.Single(Assert.Single(stored.LotView!.Contributions).Plots).AllocatedQuantity);
        Assert.Equal(SourceAllocationStatus.NotRecorded, SourceAllocation.FromSnapshot(stored.LotView)!.Status);
        Assert.Equal("SHP-1", stored.ShipmentReference);
        Assert.Equal(1, ProductionCoverage.FromSnapshot(lot).PlotCount); // the v1 coverage overload keeps working
    }
}
