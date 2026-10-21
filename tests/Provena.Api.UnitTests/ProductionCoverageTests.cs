using Provena.Api.Contracts;
using Provena.Api.Domain;
using Provena.Api.Services;
using Xunit;

namespace Provena.Api.UnitTests;

// Production Coverage v1: the pure derivation, its three adapters, and the submission-country advisory (evidence inconsistency information only).
public sealed class ProductionCoverageTests
{
    private static Guid G(int n) => new($"00000000-0000-0000-0002-{n:D12}");
    private static readonly DateOnly D1 = new(2024, 1, 1), D2 = new(2024, 6, 30), D3 = new(2024, 12, 31);

    private static PlotLinkFacts Link(int producer, int plot, string country, DateOnly? start, DateOnly? end) =>
        new(new ContributionPlot { Id = G(500 + plot), ProducerId = G(producer), ProductionPlotId = G(plot), PlotRevisionId = G(900 + plot), ProductionStartDate = start, ProductionEndDate = end },
            new PlotRevision { Id = G(900 + plot), ProductionPlotId = G(plot), Number = 1, CountryCode = country });
    private static LotContributionFacts Contribution(int producer, params PlotLinkFacts[] links) => new(new LotContribution { Id = G(100 + producer), ProducerId = G(producer), Quantity = 1m, Unit = QuantityUnit.Kg }, links);

    // Only what the advisories, the evaluator and the fingerprint read.
    private static DossierFacts Facts(string? submissionCountry, params LotContributionFacts[] contributions) =>
        new(new Shipment { Id = G(1), ReferenceNumber = "S", Quantity = 1m, Unit = QuantityUnit.Kg }, null, null, null, [], new CaseDossier { Id = G(2) }, new CaseLot { Id = G(3), Quantity = 1m, Unit = QuantityUnit.Kg }, contributions, [], null,
            submissionCountry == null ? null : new SupplierSubmission { Id = G(4), CountryOfProductionCode = submissionCountry }, [], [], []);

    [Fact]
    public void Multiple_countries_producers_and_plots_are_distinct_sorted_and_counted()
    {
        var facts = Facts("BR", Contribution(1, Link(1, 10, "CO", D1, D2), Link(1, 11, "BR", D2, D3)), Contribution(2, Link(2, 12, "BR", D1, D3)));
        var coverage = ProductionCoverage.FromFacts(facts);
        Assert.Equal(["BR", "CO"], coverage.Countries);
        Assert.Equal(D1, coverage.PeriodFrom); Assert.Equal(D3, coverage.PeriodTo);
        Assert.Equal(0, coverage.SourcesWithoutPeriod); Assert.Equal(2, coverage.ProducerCount); Assert.Equal(3, coverage.PlotCount);
    }

    [Fact]
    public void Missing_periods_are_counted_and_do_not_widen_the_envelope()
    {
        var coverage = ProductionCoverage.FromFacts(Facts(null, Contribution(1, Link(1, 10, "BR", null, null), Link(1, 11, "BR", D2, D3)), Contribution(2, Link(2, 12, "BR", null, null))));
        Assert.Equal(2, coverage.SourcesWithoutPeriod); Assert.Equal(D2, coverage.PeriodFrom); Assert.Equal(D3, coverage.PeriodTo);
        var none = ProductionCoverage.FromFacts(Facts(null, Contribution(1, Link(1, 10, "BR", null, null))));
        Assert.Null(none.PeriodFrom); Assert.Null(none.PeriodTo); Assert.Equal(1, none.SourcesWithoutPeriod);
    }

    [Fact]
    public void A_single_date_period_has_equal_endpoints_and_a_contributor_without_plots_still_counts_as_a_producer()
    {
        var coverage = ProductionCoverage.FromFacts(Facts(null, Contribution(1, Link(1, 10, "BR", D2, D2)), Contribution(2)));
        Assert.Equal(D2, coverage.PeriodFrom); Assert.Equal(D2, coverage.PeriodTo); Assert.Equal(0, coverage.SourcesWithoutPeriod);
        Assert.Equal(2, coverage.ProducerCount); Assert.Equal(1, coverage.PlotCount); Assert.Equal(["BR"], coverage.Countries);
    }

    [Fact]
    public void No_lot_or_no_sources_yields_an_empty_coverage()
    {
        var empty = ProductionCoverage.FromFacts(Facts(null) with { Dossier = null, Lot = null });
        Assert.Empty(empty.Countries); Assert.Null(empty.PeriodFrom); Assert.Null(empty.PeriodTo);
        Assert.Equal(0, empty.SourcesWithoutPeriod); Assert.Equal(0, empty.ProducerCount); Assert.Equal(0, empty.PlotCount);
        Assert.Equal(empty.Countries, ProductionCoverage.FromSnapshot(null).Countries);
        Assert.Equal(0, ProductionCoverage.FromSnapshot(null).PlotCount);
    }

    [Fact]
    public void The_snapshot_adapter_agrees_with_the_facts_adapter()
    {
        var facts = Facts("BR", Contribution(1, Link(1, 10, "CO", D1, D2)), Contribution(2, Link(2, 12, "BR", null, null)));
        var snapshot = new SnapshotLotV1(G(2), G(3), 1, "LOT", 1m, QuantityUnit.Kg, null, new(0m, 1m, QuantityReconciliationStatus.UnderAllocated), facts.Contributions.Select(c =>
            new SnapshotContributionV1(c.Contribution.ProducerId, c.Contribution.Quantity, c.Contribution.Unit, c.Links.Select(l =>
                new SnapshotPlotLinkV1(l.Link.ProductionPlotId, l.Link.PlotRevisionId, 1, l.Revision.CountryCode, PlotGeometryType.Polygon, null, "h", l.Link.ProductionStartDate, l.Link.ProductionEndDate)).ToList())).ToList());
        var fromFacts = ProductionCoverage.FromFacts(facts); var fromSnapshot = ProductionCoverage.FromSnapshot(snapshot);
        Assert.Equal(fromFacts.Countries, fromSnapshot.Countries); Assert.Equal(fromFacts.PeriodFrom, fromSnapshot.PeriodFrom); Assert.Equal(fromFacts.PeriodTo, fromSnapshot.PeriodTo);
        Assert.Equal(fromFacts.SourcesWithoutPeriod, fromSnapshot.SourcesWithoutPeriod); Assert.Equal(fromFacts.ProducerCount, fromSnapshot.ProducerCount); Assert.Equal(fromFacts.PlotCount, fromSnapshot.PlotCount);
    }

    [Theory]
    [InlineData("BR", "BR", false)]   // consistent
    [InlineData("br ", "BR", false)]  // case and padding are not a discrepancy
    [InlineData("CO", "BR", true)]    // differs
    [InlineData(null, "BR", false)]   // not comparable: no submission country
    [InlineData("", "BR", false)]
    public void The_advisory_compares_only_with_the_submission_country(string? submission, string source, bool differs)
    {
        var facts = Facts("ignored", Contribution(1, Link(1, 10, source, D1, D2))) with { Submission = new SupplierSubmission { Id = G(4), CountryOfProductionCode = submission } };
        var advisories = CompletenessService.Advisories(facts, false).Where(a => a.Code == ProductionCoverage.SubmissionCountryDiffersCode).ToList();
        Assert.Equal(differs, advisories.Count == 1);
        if (differs) { Assert.Equal("Production", advisories[0].Area); Assert.Equal(G(4), advisories[0].SubjectId); Assert.Contains("BR", advisories[0].Message); Assert.Contains("CO", advisories[0].Message); }
    }

    [Fact]
    public void Sources_in_more_countries_than_the_single_submission_country_differ_and_no_sources_or_no_submission_are_not_comparable()
    {
        Assert.True(ProductionCoverage.DiffersFromSubmission(ProductionCoverage.Derive([G(1)], [new(G(1), G(10), "BR", null, null), new(G(1), G(11), "CO", null, null)]), "BR"));
        Assert.False(ProductionCoverage.DiffersFromSubmission(ProductionCoverage.Empty, "BR"));
        Assert.DoesNotContain(CompletenessService.Advisories(Facts("BR"), false), a => a.Code == ProductionCoverage.SubmissionCountryDiffersCode);
        var noSubmission = Facts(null, Contribution(1, Link(1, 10, "BR", D1, D2)));
        Assert.DoesNotContain(CompletenessService.Advisories(noSubmission, false), a => a.Code == ProductionCoverage.SubmissionCountryDiffersCode);
    }

    [Fact]
    public void The_advisory_never_changes_the_completeness_axes_items_or_fingerprint()
    {
        var consistent = Facts("BR", Contribution(1, Link(1, 10, "BR", D1, D2)));
        var differing = consistent with { Submission = new SupplierSubmission { Id = G(4), CountryOfProductionCode = "CO" } };
        var a = CompletenessEvaluator.Evaluate(consistent); var b = CompletenessEvaluator.Evaluate(differing);
        Assert.Equal(a.DataCoverage, b.DataCoverage); Assert.Equal(a.EvidenceReadiness, b.EvidenceReadiness);
        Assert.Equal(a.Items.Select(i => (i.Code, i.Status)).ToList(), b.Items.Select(i => (i.Code, i.Status)).ToList());
        Assert.Equal(DossierFingerprint.Compute(consistent).SectionHashes["Lot"], DossierFingerprint.Compute(differing).SectionHashes["Lot"]); // the submission country is not part of the Lot section
        Assert.Contains(CompletenessService.Advisories(differing, false), x => x.Code == ProductionCoverage.SubmissionCountryDiffersCode);
        Assert.DoesNotContain(CompletenessService.Advisories(consistent, false), x => x.Code == ProductionCoverage.SubmissionCountryDiffersCode);
    }

    [Fact]
    public void Lot_input_normalization_keeps_the_expected_lot_id()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, CaseDossierService.Normalize(new CaseLotInput(" LOT ", 1m, QuantityUnit.Kg, null, [], id)).ExpectedCurrentLotId);
        Assert.Null(CaseDossierService.Normalize(new CaseLotInput("LOT", 1m, QuantityUnit.Kg, null, [], null)).ExpectedCurrentLotId);
    }
}
