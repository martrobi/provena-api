using System.Data;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Orchestrates the read-only completeness model of the CURRENT working dossier (ADR 0008): facts -> pure evaluation -> pure fingerprint -> advisories.
// Computed on every read and never stored; GET writes nothing (no audit, no UpdatedAtUtc). It decides nothing about legal compliance, risk or release,
// and it never changes the case status or any requirement. Review freshness and the latest review are not available yet (always None / null).
public sealed class CompletenessService(AppDbContext db, DossierFactsBuilder builder)
{
    // Standalone read: a repeatable-read snapshot gives one coherent view across the builder's queries without taking any lock.
    public async Task<CompletenessDto> Get(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct) ?? throw ApiException.NotFound();
        return await Compute(c, ct);
    }

    // For callers that already hold a transaction (for example the working export under the case lock).
    public async Task<CompletenessDto> Compute(EudrCase c, CancellationToken ct)
    {
        var facts = await builder.Build(c, ct);
        var evaluation = CompletenessEvaluator.Evaluate(facts);
        var fingerprint = DossierFingerprint.Compute(facts);
        return new(CompletenessPolicy.Dto(), DateTimeOffset.UtcNow, fingerprint.Fingerprint, fingerprint.Version, new(evaluation.ApplicabilityStatus, facts.Applicability?.Id),
            evaluation.DataCoverage, evaluation.EvidenceReadiness, ReviewFreshness.None, null, Advisories(facts, c.Status is EudrCaseStatus.Completed or EudrCaseStatus.Archived), evaluation.Items);
    }

    // Advisories never make a case incomplete and never enter the fingerprint.
    public static List<CompletenessAdvisoryDto> Advisories(DossierFacts facts, bool caseClosed)
    {
        var advisories = new List<CompletenessAdvisoryDto>();
        var closure = caseClosed ? " The case is closed: this is shown after case closure and the reviewed record is unaffected." : "";
        foreach (var drift in facts.Drift)
            advisories.Add(new($"CATALOGUE_DRIFT_{drift.Area.ToString().ToUpperInvariant()}", drift.Area.ToString(), null,
                $"The {drift.Area.ToString().ToLowerInvariant()} catalogue values changed since the case master data was copied (fields: {string.Join(", ", drift.Fields)}). Advisory only; refresh the case master data to adopt them." + closure));
        // The case adopted these immutable revisions explicitly; moving to a newer revision needs a deliberate lot save.
        foreach (var revision in facts.Contributions.SelectMany(c => c.Links).Select(l => l.Revision).DistinctBy(r => r.Id).Where(r => r.SupersededAtUtc != null).OrderBy(r => r.Id))
            advisories.Add(new("PINNED_REVISION_SUPERSEDED", "Lot", revision.Id,
                $"The pinned plot revision {revision.Number} has since been superseded by a newer revision. The case keeps using the pinned one until the lot is saved with another revision." + closure));
        return advisories;
    }
}
