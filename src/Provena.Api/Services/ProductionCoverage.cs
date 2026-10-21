using Provena.Api.Contracts;
namespace Provena.Api.Services;
// Pure, derived-only production coverage of one lot version (never stored, never fingerprinted, never an input to completeness). Every input is a fact the dossier fingerprint
// already covers (the pinned revision's country, the per-source period and the producer/plot ids), so a coverage change always coincides with a fingerprint change.
// Production country stays source-level data: there is no case-level country. It is evidence arithmetic, not a legal conclusion.
public sealed record CoverageSource(Guid ProducerId, Guid ProductionPlotId, string CountryCode, DateOnly? ProductionStartDate, DateOnly? ProductionEndDate);

public static class ProductionCoverage
{
    public const string Scope = "Derived production coverage of the recorded lot sources (distinct source countries, period envelope and counts). Evidence information only; not a legal determination, risk assessment or compliance finding.";
    public const string SubmissionCountryDiffersCode = "PRODUCTION_COUNTRY_DIFFERS_FROM_SUBMISSION";

    // producerIds: every contributing producer (including one with no plot link). sources: one entry per pinned plot link.
    public static ProductionCoverageDto Derive(IEnumerable<Guid> producerIds, IEnumerable<CoverageSource> sources)
    {
        var links = sources.ToList();
        var starts = links.Where(s => s.ProductionStartDate != null).Select(s => s.ProductionStartDate!.Value).ToList();
        var ends = links.Where(s => s.ProductionEndDate != null).Select(s => s.ProductionEndDate!.Value).ToList();
        return new(links.Select(s => s.CountryCode).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            starts.Count == 0 ? null : starts.Min(), ends.Count == 0 ? null : ends.Max(),
            links.Count(s => s.ProductionStartDate == null || s.ProductionEndDate == null),
            producerIds.Distinct().Count(), links.Select(s => s.ProductionPlotId).Distinct().Count());
    }

    public static ProductionCoverageDto Empty { get; } = Derive([], []);

    public static ProductionCoverageDto FromFacts(DossierFacts f) =>
        Derive(f.Contributions.Select(c => c.Contribution.ProducerId), f.Contributions.SelectMany(c => c.Links.Select(l => new CoverageSource(c.Contribution.ProducerId, l.Link.ProductionPlotId, l.Revision.CountryCode, l.Link.ProductionStartDate, l.Link.ProductionEndDate))));

    public static ProductionCoverageDto FromLot(CaseLotDto lot) =>
        Derive(lot.Contributions.Select(c => c.ProducerId), lot.Contributions.SelectMany(c => c.Plots.Select(p => new CoverageSource(c.ProducerId, p.ProductionPlotId, p.CountryCode, p.ProductionStartDate, p.ProductionEndDate))));

    // The retained review snapshot only: never a live producer, plot, revision or lot row.
    public static ProductionCoverageDto FromSnapshot(SnapshotLotV1? lot) => lot == null ? Empty :
        Derive(lot.Contributions.Select(c => c.ProducerId), lot.Contributions.SelectMany(c => c.Plots.Select(p => new CoverageSource(c.ProducerId, p.PlotId, p.CountryCode, p.ProductionStartDate, p.ProductionEndDate))));

    // Comparable only when there is at least one source country and the supplier submission states a country. The submission states exactly one country of production,
    // so the sources agree only when their distinct countries are exactly that one. Informational: the supplier's statement is never overridden.
    public static bool DiffersFromSubmission(ProductionCoverageDto coverage, string? submissionCountry)
    {
        var stated = submissionCountry?.Trim();
        if (string.IsNullOrEmpty(stated) || coverage.Countries.Count == 0) return false;
        return coverage.Countries.Count != 1 || !string.Equals(coverage.Countries[0], stated, StringComparison.OrdinalIgnoreCase);
    }
}
