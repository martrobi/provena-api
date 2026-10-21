using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Pure helpers and validators of the case dossier (Slice 4 / commit 2): reconciliation, order-independent equality, structural validation.
public sealed class DossierDomainTests
{
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-000000000001"), P2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Plot1 = Guid.NewGuid(), Plot2 = Guid.NewGuid(), Rev1 = Guid.NewGuid(), Rev1b = Guid.NewGuid(), Rev2 = Guid.NewGuid();
    private static readonly DateOnly D1 = new(2024, 1, 1), D2 = new(2024, 12, 31);
    private static readonly CaseLotValidator Validator = new();

    private static ContributionPlotInput Link(Guid plot, Guid revision, DateOnly? start = null, DateOnly? end = null) => new(plot, revision, start, end);
    private static LotContributionInput Contribution(Guid producer, decimal quantity, params ContributionPlotInput[] plots) => new(producer, quantity, QuantityUnit.Kg, [.. plots]);
    private static CaseLotInput Lot(decimal quantity = 10m, string reference = "LOT-1", string? note = null, params LotContributionInput[] contributions) => new(reference, quantity, QuantityUnit.Kg, note, [.. contributions], null);

    // The stored form of an input: one lot row, contribution rows and link rows with generated ids.
    private static (CaseLot Lot, List<LotContribution> Contributions, List<ContributionPlot> Links) Stored(CaseLotInput input)
    {
        var lot = new CaseLot { LotReference = input.LotReference, Quantity = input.Quantity, Unit = input.Unit, Note = input.Note };
        var contributions = new List<LotContribution>(); var links = new List<ContributionPlot>();
        foreach (var c in input.Contributions)
        {
            var row = new LotContribution { CaseLotId = lot.Id, ProducerId = c.ProducerId, Quantity = c.Quantity, Unit = c.Unit }; contributions.Add(row);
            links.AddRange(c.Plots.Select(p => new ContributionPlot { LotContributionId = row.Id, ProducerId = c.ProducerId, ProductionPlotId = p.ProductionPlotId, PlotRevisionId = p.PlotRevisionId, ProductionStartDate = p.ProductionStartDate, ProductionEndDate = p.ProductionEndDate }));
        }
        return (lot, contributions, links);
    }
    private static bool Identical(CaseLotInput stored, CaseLotInput submitted)
    { var (lot, contributions, links) = Stored(stored); return CaseDossierService.IsIdentical(CaseDossierService.Normalize(submitted), lot, contributions, links); }
    private static CaseLotInput Base() => Lot(10m, "LOT-1", "note",
        Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)));

    [Fact]
    public void Reconcile_uses_exact_decimal_equality_and_derives_status()
    {
        var reconciled = CaseDossierService.Reconcile(10m, [6m, 4m]);
        Assert.Equal(QuantityReconciliationStatus.Reconciled, reconciled.Status); Assert.Equal(10m, reconciled.AllocatedQuantity); Assert.Equal(0m, reconciled.RemainingQuantity);
        Assert.Equal(QuantityReconciliationStatus.Reconciled, CaseDossierService.Reconcile(1.5m, [1.500000m]).Status);
        var under = CaseDossierService.Reconcile(10m, [6m, 3.5m]);
        Assert.Equal(QuantityReconciliationStatus.UnderAllocated, under.Status); Assert.Equal(9.5m, under.AllocatedQuantity); Assert.Equal(0.5m, under.RemainingQuantity);
        var none = CaseDossierService.Reconcile(10m, []);
        Assert.Equal(QuantityReconciliationStatus.UnderAllocated, none.Status); Assert.Equal(0m, none.AllocatedQuantity); Assert.Equal(10m, none.RemainingQuantity);
        Assert.Equal(QuantityReconciliationStatus.UnderAllocated, CaseDossierService.Reconcile(1m, [0.999999m]).Status);
        Assert.Equal(400, Assert.Throws<ApiException>(() => CaseDossierService.Reconcile(10m, [6m, 4.000001m])).Status);
    }

    [Fact]
    public void Normalize_trims_the_reference_and_turns_a_blank_note_into_null()
    {
        var normalized = CaseDossierService.Normalize(Lot(1m, "  LOT-9  ", "   "));
        Assert.Equal("LOT-9", normalized.LotReference); Assert.Null(normalized.Note);
        Assert.Equal("keep", CaseDossierService.Normalize(Lot(1m, "x", "keep")).Note);
        Assert.Equal("", CaseDossierService.Normalize(new CaseLotInput(null!, 1m, QuantityUnit.Kg, null, [], null)).LotReference);
    }

    [Fact]
    public void Equality_ignores_ordering_of_contributions_and_of_plots()
    {
        Assert.True(Identical(Base(), Base()));
        var reorderedContributions = Lot(10m, "LOT-1", "note", Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)), Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2)));
        Assert.True(Identical(Base(), reorderedContributions));
        var reorderedPlots = Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot2, Rev2), Link(Plot1, Rev1, D1, D2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)));
        Assert.True(Identical(Base(), reorderedPlots));
    }

    [Fact]
    public void Equality_compares_decimal_values_and_normalized_text()
    {
        Assert.True(Identical(Lot(1.5m, "LOT-1", null), Lot(1.500000m, "  LOT-1 ", "  ")));
        var stored = Lot(10m, "LOT-1", null, Contribution(P1, 1.5m));
        Assert.True(Identical(stored, Lot(10.000000m, "LOT-1", null, Contribution(P1, 1.500000m))));
    }

    [Fact]
    public void Equality_detects_every_factual_change()
    {
        Assert.False(Identical(Base(), Lot(10m, "LOT-2", "note", Base().Contributions.ToArray())));
        Assert.False(Identical(Base(), Lot(11m, "LOT-1", "note", Base().Contributions.ToArray())));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "other", Base().Contributions.ToArray())));
        Assert.False(Identical(Base(), Base() with { Unit = QuantityUnit.Ton }));
        // Contribution quantity, unit, producer, added / removed producer.
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 5m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)) with { Unit = QuantityUnit.Ton })));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Base().Contributions.Append(Contribution(Guid.NewGuid(), 0.5m)).ToArray())));
        // Changed production date, changed revision of the same plot, added / removed plot link.
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1, D1, D1), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1b, D1, D2), Link(Plot2, Rev2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
        Assert.False(Identical(Base(), Lot(10m, "LOT-1", "note", Contribution(P1, 6m, Link(Plot1, Rev1, D1, D2), Link(Plot2, Rev2), Link(Guid.NewGuid(), Rev1b)), Contribution(P2, 4m, Link(Plot2, Rev2, D1, D1)))));
    }

    private static CaseLotInput Valid() => Lot(10m, "LOT-1", null, Contribution(P1, 10m, Link(Plot1, Rev1)));
    private static string[] Errors(CaseLotInput input) => Validator.Validate(CaseDossierService.Normalize(input)).Errors.Select(e => e.ErrorMessage).ToArray();

    [Fact]
    public void Validator_accepts_a_structurally_valid_lot_including_no_contributions_and_equal_start_end()
    {
        Assert.Empty(Errors(Valid()));
        Assert.Empty(Errors(Lot(10m)));
        Assert.Empty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1, D1, D1)))));
        Assert.Empty(Errors(Lot(99999999999999.999999m, "L")));
    }

    [Theory]
    [InlineData("")] [InlineData("   ")]
    public void Validator_requires_a_reference(string reference) => Assert.NotEmpty(Errors(Valid() with { LotReference = reference }));

    [Fact]
    public void Validator_limits_reference_note_quantity_precision_and_units()
    {
        Assert.NotEmpty(Errors(Valid() with { LotReference = new string('x', 101) }));
        Assert.Empty(Errors(Valid() with { LotReference = new string('x', 100) }));
        Assert.NotEmpty(Errors(Valid() with { Note = new string('x', 4001) }));
        foreach (var quantity in new[] { 0m, -1m, 100000000000000m, 1.1234567m })
            Assert.NotEmpty(Errors(Valid() with { Quantity = quantity }));
        Assert.NotEmpty(Errors(Valid() with { Unit = QuantityUnit.Other, Contributions = [] }));
        Assert.NotEmpty(Errors(Valid() with { Unit = (QuantityUnit)99, Contributions = [] }));
        var other = Valid(); other = other with { Contributions = [other.Contributions[0] with { Unit = QuantityUnit.Other }] };
        Assert.NotEmpty(Errors(other));
        foreach (var quantity in new[] { 0m, -1m, 100000000000000m, 1.1234567m })
            Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, quantity))));
    }

    [Fact]
    public void Validator_requires_contribution_units_equal_to_the_lot_unit_without_conversion()
    {
        var mismatch = Lot(10m, "L", null, Contribution(P1, 1m) with { Unit = QuantityUnit.Ton });
        Assert.Contains(Errors(mismatch), e => e.Contains("must equal the lot unit"));
    }

    [Fact]
    public void Validator_rejects_duplicates_missing_collections_and_empty_ids()
    {
        Assert.Contains(Errors(Lot(10m, "L", null, Contribution(P1, 1m), Contribution(P1, 2m))), e => e.Contains("producer may appear only once"));
        Assert.Contains(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1), Link(Plot1, Rev1b)))), e => e.Contains("plot may appear only once"));
        Assert.NotEmpty(Errors(Valid() with { Contributions = null! }));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, new LotContributionInput(P1, 1m, QuantityUnit.Kg, null!))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(Guid.Empty, 1m))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Guid.Empty, Rev1)))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Guid.Empty)))));
    }

    [Fact]
    public void Validator_enforces_the_production_period_pair_and_order()
    {
        Assert.Empty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1)))));
        Assert.Empty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1, D1, D2)))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1, D1, null)))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1, null, D2)))));
        Assert.NotEmpty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Link(Plot1, Rev1, D2, D1)))));
    }

    [Fact]
    public void Validator_applies_the_structural_caps()
    {
        Assert.Empty(Errors(Lot(10m, "L", null, Enumerable.Range(0, 200).Select(_ => Contribution(Guid.NewGuid(), 0.01m)).ToArray())));
        Assert.Contains(Errors(Lot(10m, "L", null, Enumerable.Range(0, 201).Select(_ => Contribution(Guid.NewGuid(), 0.01m)).ToArray())), e => e.Contains("200 contributions"));
        ContributionPlotInput[] Links(int n) => Enumerable.Range(0, n).Select(_ => Link(Guid.NewGuid(), Guid.NewGuid())).ToArray();
        Assert.Empty(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Links(500)))));
        Assert.Contains(Errors(Lot(10m, "L", null, Contribution(P1, 1m, Links(501)))), e => e.Contains("500 plot links"));
        // 11 contributions of 500 links exceed the 5000 total.
        Assert.Contains(Errors(Lot(10m, "L", null, Enumerable.Range(0, 11).Select(_ => Contribution(Guid.NewGuid(), 0.01m, Links(500))).ToArray())), e => e.Contains("5000 plot links"));
        Assert.Empty(Errors(Lot(10m, "L", null, Enumerable.Range(0, 10).Select(_ => Contribution(Guid.NewGuid(), 0.01m, Links(500))).ToArray())));
    }

}
