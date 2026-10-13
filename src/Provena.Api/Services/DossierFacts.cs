using Provena.Api.Contracts;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// The typed, case-owned facts of one case at one moment (ADR 0008), built by DossierFactsBuilder and consumed by the pure CompletenessEvaluator
// and DossierFingerprint. Rows are detached copies of case-owned or immutable data; the live catalogue appears only as the precomputed Drift list
// (advisory, never fingerprinted). Producer/plot labels, active flags and case status are deliberately absent.
public sealed record PlotLinkFacts(ContributionPlot Link, PlotRevision Revision);
public sealed record LotContributionFacts(LotContribution Contribution, IReadOnlyList<PlotLinkFacts> Links);
public sealed record DossierFacts(
    Shipment Shipment,
    CaseMasterDataVersion? MasterData,
    CaseApplicability? Applicability,
    CaseRecipientStatement? Recipients,
    IReadOnlyList<CaseRecipient> RecipientEntries,
    CaseDossier? Dossier,
    CaseLot? Lot,
    IReadOnlyList<LotContributionFacts> Contributions,
    IReadOnlyList<CaseDocument> Documents,
    GeoSubmission? Geolocation,
    SupplierSubmission? Submission,
    IReadOnlyList<CaseEvidenceLink> EvidenceLinks,
    IReadOnlyList<EudrRequirement> Requirements,
    IReadOnlyList<MasterDataDriftDto> Drift);
