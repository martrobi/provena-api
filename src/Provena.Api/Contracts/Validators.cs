using FluentValidation;
using Provena.Api.Domain;
namespace Provena.Api.Contracts;
public sealed class RegisterValidator : AbstractValidator<RegisterRequest>
{
    public RegisterValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(100); RuleFor(x => x.LastName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.OrganizationName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Oib).Matches(@"^\d{11}$").When(x => !string.IsNullOrEmpty(x.Oib));
    }
}
public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator() { RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256); RuleFor(x => x.Password).NotEmpty().MaximumLength(128); }
}
public sealed class SupplierValidator : AbstractValidator<SupplierInput>
{
    public SupplierValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.ExternalReference).MaximumLength(100);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrEmpty(x.Email));
        RuleFor(x => x.PostalAddress).MaximumLength(1000); RuleFor(x => x.ContactPerson).MaximumLength(200); RuleFor(x => x.CountryCode).Matches("^[A-Z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryCode));
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
public sealed class ProductValidator : AbstractValidator<ProductInput>
{
    public ProductValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.ExternalReference).MaximumLength(100);
        RuleFor(x => x.CnCode).NotEmpty().Matches("^[0-9]{4,10}$"); RuleFor(x => x.CommodityType).IsInEnum();
        RuleFor(x => x.Description).MaximumLength(4000);
    }
}
public sealed class ShipmentValidator : AbstractValidator<ShipmentInput>
{
    public ShipmentValidator()
    {
        RuleFor(x => x.ReferenceNumber).NotEmpty().MaximumLength(100); RuleFor(x => x.SupplierId).NotEmpty(); RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).LessThan(100000000000000m).PrecisionScale(20, 6, true);
        RuleFor(x => x.Unit).IsInEnum(); RuleFor(x => x.OriginCountryCode).Matches("^[A-Z]{2}$").When(x => !string.IsNullOrEmpty(x.OriginCountryCode));
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
public sealed class SubmissionValidator : AbstractValidator<SubmissionInput>
{
    public SubmissionValidator()
    {
        RuleFor(x => x.CountryOfProductionCode).Matches("^[A-Z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryOfProductionCode));
        RuleFor(x => x.ConfirmedQuantity).GreaterThan(0).LessThan(100000000000000m).PrecisionScale(20, 6, true).When(x => x.ConfirmedQuantity.HasValue);
        RuleFor(x => x).Must(x => x.ProductionPeriodFrom == null || x.ProductionPeriodTo == null || x.ProductionPeriodFrom <= x.ProductionPeriodTo).WithMessage("Production period from must be on or before production period to.");
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
public sealed class ReviewValidator : AbstractValidator<ReviewInput>
{
    public ReviewValidator() { RuleFor(x => x.Status).Must(x => x is RequirementStatus.Accepted or RequirementStatus.Rejected or RequirementStatus.NotRequired); RuleFor(x => x.ReviewerNote).MaximumLength(4000); }
}
public sealed class ProducerValidator : AbstractValidator<ProducerInput>
{
    public ProducerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.ExternalReference).MaximumLength(100);
        RuleFor(x => x.CountryCode).Matches("^[A-Z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryCode));
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
public sealed class PlotRevisionValidator : AbstractValidator<PlotRevisionInput>
{
    public PlotRevisionValidator()
    {
        RuleFor(x => x.CountryCode).NotEmpty().Matches("^[A-Z]{2}$");
        RuleFor(x => x.Geometry).Must(x => x.ValueKind == System.Text.Json.JsonValueKind.Object).WithMessage("Geometry must be a GeoJSON geometry object.");
        RuleFor(x => x.AreaHectares).GreaterThan(0).LessThan(100000000m).PrecisionScale(12, 4, true).When(x => x.AreaHectares.HasValue);
        RuleFor(x => x.Note).MaximumLength(4000);
    }
}
public sealed class ProductionPlotValidator : AbstractValidator<ProductionPlotInput>
{
    public ProductionPlotValidator()
    {
        RuleFor(x => x.ProducerId).NotEmpty(); RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.ExternalReference).MaximumLength(100);
        RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.InitialRevision).NotNull().SetValidator(new PlotRevisionValidator());
    }
}
public sealed class ProductionPlotUpdateValidator : AbstractValidator<ProductionPlotUpdateInput>
{
    public ProductionPlotUpdateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.ExternalReference).MaximumLength(100); RuleFor(x => x.Notes).MaximumLength(4000);
        RuleFor(x => x.IsActive).NotNull().WithMessage("isActive is required.");
    }
}
// Structural / factual validation of a case lot only: no completeness, sufficiency or cut-off-date rules. Production period: both dates or neither, start <= end.
public sealed class ContributionPlotValidator : AbstractValidator<ContributionPlotInput>
{
    public ContributionPlotValidator()
    {
        RuleFor(x => x.ProductionPlotId).NotEmpty(); RuleFor(x => x.PlotRevisionId).NotEmpty();
        RuleFor(x => x.ProductionEndDate).NotNull().When(x => x.ProductionStartDate.HasValue).WithMessage("Production end date is required when a start date is given.");
        RuleFor(x => x.ProductionStartDate).NotNull().When(x => x.ProductionEndDate.HasValue).WithMessage("Production start date is required when an end date is given.");
        RuleFor(x => x.ProductionEndDate).Must((x, end) => x.ProductionStartDate <= end).When(x => x.ProductionStartDate.HasValue && x.ProductionEndDate.HasValue).WithMessage("Production start date must be on or before the end date.");
    }
}
public sealed class LotContributionValidator : AbstractValidator<LotContributionInput>
{
    public LotContributionValidator()
    {
        RuleFor(x => x.ProducerId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).LessThan(100000000000000m).PrecisionScale(20, 6, true);
        RuleFor(x => x.Unit).IsInEnum().NotEqual(QuantityUnit.Other).WithMessage("Unit 'Other' is not supported for lot quantities.");
        RuleFor(x => x.Plots).NotNull().Must(p => p == null || p.Count <= 500).WithMessage("At most 500 plot links are allowed per contribution.");
        RuleForEach(x => x.Plots).NotNull().SetValidator(new ContributionPlotValidator()).When(x => x.Plots != null);
        RuleFor(x => x.Plots).Must(p => p!.Select(l => l?.ProductionPlotId).Distinct().Count() == p!.Count).When(x => x.Plots != null).WithMessage("A production plot may appear only once per contribution.");
    }
}
public sealed class CaseLotValidator : AbstractValidator<CaseLotInput>
{
    public CaseLotValidator()
    {
        RuleFor(x => x.LotReference).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Quantity).GreaterThan(0).LessThan(100000000000000m).PrecisionScale(20, 6, true);
        RuleFor(x => x.Unit).IsInEnum().NotEqual(QuantityUnit.Other).WithMessage("Unit 'Other' is not supported for lot quantities.");
        RuleFor(x => x.Note).MaximumLength(4000);
        RuleFor(x => x.Contributions).NotNull().Must(c => c == null || c.Count <= 200).WithMessage("At most 200 contributions are allowed.");
        RuleForEach(x => x.Contributions).NotNull().SetValidator(new LotContributionValidator()).When(x => x.Contributions != null);
        RuleFor(x => x.Contributions).Must(c => c!.Select(i => i?.ProducerId).Distinct().Count() == c!.Count).When(x => x.Contributions != null).WithMessage("A producer may appear only once per lot.");
        RuleFor(x => x.Contributions).Must(c => c!.Sum(i => i?.Plots?.Count ?? 0) <= 5000).When(x => x.Contributions != null).WithMessage("At most 5000 plot links are allowed per lot.");
        RuleForEach(x => x.Contributions).Must((lot, c) => c == null || c.Unit == lot.Unit).When(x => x.Contributions != null).WithMessage("Contribution unit must equal the lot unit; units are not converted.");
    }
}
// Completeness facts (ADR 0008). Structural / factual validation only: no completeness, sufficiency or legal-compliance rules.
public sealed class OperatorProfileValidator : AbstractValidator<OperatorProfileInput>
{
    public OperatorProfileValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.PostalAddress).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(x => x.EstablishmentCountryCode).NotEmpty().Matches("^[A-Z]{2}$");
        RuleFor(x => x.EoriNumber).Matches("^[A-Z0-9]{1,17}$").When(x => x.EoriNumber != null);
    }
}
// Runs on the normalized input (trimmed, blank optionals null).
public sealed class CaseApplicabilityValidator : AbstractValidator<CaseApplicabilityInput>
{
    public CaseApplicabilityValidator()
    {
        RuleFor(x => x.ActivityType).IsInEnum(); RuleFor(x => x.OperatorRole).IsInEnum(); RuleFor(x => x.ActivityDateKind).IsInEnum();
        RuleFor(x => x.RegulatoryScope).IsInEnum(); RuleFor(x => x.FlowSupport).IsInEnum();
        RuleFor(x => x.ProductDescription).MaximumLength(2000); RuleFor(x => x.OperatorRoleDescription).MaximumLength(500);
        RuleFor(x => x.ScopeCitation).MaximumLength(1000); RuleFor(x => x.Qualifications).MaximumLength(2000);
        RuleFor(x => x.LegalBasisVersion).MaximumLength(200); RuleFor(x => x.Reasoning).MaximumLength(4000);
        RuleFor(x => x.OperatorRoleDescription).NotEmpty().When(x => x.OperatorRole == OperatorRole.OtherUnsupported).WithMessage("A description of the operator's role is required when the role is OtherUnsupported.");
        RuleFor(x => x.OperatorRoleDescription).Null().When(x => x.OperatorRole == OperatorRole.Operator).WithMessage("A role description is only permitted when the role is OtherUnsupported.");
        RuleFor(x => x.ProductDescription).NotEmpty().When(x => x.RegulatoryScope == RegulatoryScope.InScope).WithMessage("A product description is required when the regulatory scope is InScope.");
        When(x => x.RegulatoryScope != RegulatoryScope.Pending, () =>
        {
            RuleFor(x => x.ScopeCitation).NotEmpty(); RuleFor(x => x.LegalBasisVersion).NotEmpty(); RuleFor(x => x.Reasoning).NotEmpty();
            RuleFor(x => x.EffectiveDate).NotNull(); RuleFor(x => x.ActivityDate).NotNull();
        });
    }
}
public sealed class CaseRecipientValidator : AbstractValidator<CaseRecipientInput>
{
    public CaseRecipientValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200); RuleFor(x => x.PostalAddress).NotEmpty().MaximumLength(1000);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
    }
}
// Runs on the normalized input. RecipientsIdentified: 1..50 complete recipients and no basis. NotApplicable: no recipients and a recorded basis.
public sealed class CaseRecipientsValidator : AbstractValidator<CaseRecipientsInput>
{
    public CaseRecipientsValidator()
    {
        RuleFor(x => x.Resolution).IsInEnum();
        RuleFor(x => x.NotApplicableBasis).MaximumLength(2000);
        RuleFor(x => x.Recipients).NotNull();
        RuleForEach(x => x.Recipients).NotNull().SetValidator(new CaseRecipientValidator()).When(x => x.Recipients != null);
        When(x => x.Resolution == RecipientResolution.RecipientsIdentified, () =>
        {
            RuleFor(x => x.Recipients).Must(r => r == null || (r.Count >= 1 && r.Count <= 50)).WithMessage("Between 1 and 50 recipients are required when recipients are identified.");
            RuleFor(x => x.NotApplicableBasis).Null().WithMessage("A not-applicable basis is only permitted when the resolution is NotApplicable.");
        });
        When(x => x.Resolution == RecipientResolution.NotApplicable, () =>
        {
            RuleFor(x => x.Recipients).Must(r => r == null || r.Count == 0).WithMessage("No recipients may be given when the resolution is NotApplicable.");
            RuleFor(x => x.NotApplicableBasis).NotEmpty().WithMessage("A basis is required when the resolution is NotApplicable.");
        });
    }
}
public sealed class CaseEvidenceLinkValidator : AbstractValidator<CaseEvidenceLinkInput>
{
    public CaseEvidenceLinkValidator() { RuleFor(x => x.CaseDocumentId).NotEmpty(); RuleFor(x => x.Purpose).IsInEnum(); RuleFor(x => x.Note).MaximumLength(1000); }
}
// Reviewed dossier history (ADR 0008). Runs on the trimmed input. ExpectedFingerprint is the 64-character SHA-256 hex the Owner loaded.
public sealed class CaseReviewValidator : AbstractValidator<CaseReviewInput>
{
    public CaseReviewValidator()
    {
        RuleFor(x => x.Decision).IsInEnum();
        RuleFor(x => x.Rationale).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.ExpectedFingerprint).NotEmpty().Matches("^[0-9a-f]{64}$").WithMessage("expectedFingerprint must be the 64-character hexadecimal fingerprint returned by the completeness endpoint.");
    }
}

// Case Lifecycle v2. Run on the trimmed input (a blank DDS reference was already turned into null); reasons are required and non-blank. The outcome-dependent DDS-reference rule
// lives in CaseClosurePolicy because it needs the outcome derived from the review.
public sealed class CloseCaseValidator : AbstractValidator<CloseCaseInput>
{
    public CloseCaseValidator()
    {
        RuleFor(x => x.ExpectedReviewId).NotNull().NotEqual(Guid.Empty).WithMessage("expectedReviewId is the id of the latest review the Owner loaded.");
        RuleFor(x => x.ExternalDdsReference).MaximumLength(200);
    }
}
public sealed class ReopenCaseValidator : AbstractValidator<ReopenCaseInput>
{
    public ReopenCaseValidator()
    {
        RuleFor(x => x.ExpectedClosureId).NotNull().NotEqual(Guid.Empty).WithMessage("expectedClosureId is the id of the current closure the Owner loaded.");
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000);
    }
}
public sealed class ArchiveCaseValidator : AbstractValidator<ArchiveCaseInput>
{
    public ArchiveCaseValidator() { RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000); }
}
public sealed class UnarchiveCaseValidator : AbstractValidator<UnarchiveCaseInput>
{
    public UnarchiveCaseValidator() { RuleFor(x => x.Reason).NotEmpty().MaximumLength(4000); }
}

// External DDS Record v1. Runs on the trimmed input (SubmittedAtUtc is already normalized to UTC). The values are never echoed in messages. The future-time rule needs the clock and the
// replacement-reason rule needs to know whether a record exists, so ExternalDdsService applies them with the same 400 shape.
public sealed class CreateExternalDdsRecordValidator : AbstractValidator<CreateExternalDdsRecordInput>
{
    public CreateExternalDdsRecordValidator()
    {
        RuleFor(x => x.ReviewId).NotNull().NotEqual(Guid.Empty).WithMessage("reviewId is the id of the latest Owner review that was submitted externally.");
        RuleFor(x => x.ReferenceNumber).NotEmpty().MaximumLength(200);
        RuleFor(x => x.VerificationNumber).NotEmpty().MaximumLength(200);
        RuleFor(x => x.SubmittedAtUtc).NotNull().WithMessage("submittedAtUtc is the time the declaration was submitted outside Provena.");
        RuleFor(x => x.ExpectedLatestExternalDdsRecordId).NotEqual(Guid.Empty);
        RuleFor(x => x.ReceiptDocumentId).NotEqual(Guid.Empty);
        RuleFor(x => x.ReplacementReason).MaximumLength(4000);
    }
}

// Post-completion Concern & Corrective Action v1. Run on the trimmed input (instants already normalized to UTC). Free text is required and non-blank; values are never echoed in messages.
// The future-time rules need the clock, so ConcernService applies them with the same 400 shape.
public sealed class CreateConcernValidator : AbstractValidator<CreateConcernInput>
{
    public CreateConcernValidator()
    {
        RuleFor(x => x.Category).NotNull().IsInEnum();
        RuleFor(x => x.Summary).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Details).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.RaisedAtUtc).NotNull().WithMessage("raisedAtUtc is the time the concern was raised.");
    }
}
public sealed class RecordConcernActionValidator : AbstractValidator<RecordConcernActionInput>
{
    public RecordConcernActionValidator()
    {
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.PerformedAtUtc).NotNull().WithMessage("performedAtUtc is the time the action was performed.");
        RuleFor(x => x.EvidenceDocumentId).NotEqual(Guid.Empty);
    }
}
public sealed class RecordConcernNotificationValidator : AbstractValidator<RecordConcernNotificationInput>
{
    public RecordConcernNotificationValidator()
    {
        RuleFor(x => x.RecipientId).NotNull().NotEqual(Guid.Empty).WithMessage("recipientId is the id of a recipient of this case.");
        RuleFor(x => x.Channel).NotNull().IsInEnum();
        RuleFor(x => x.NotifiedAtUtc).NotNull().WithMessage("notifiedAtUtc is the time the recipient was notified.");
        RuleFor(x => x.Note).MaximumLength(1000);
        RuleFor(x => x.EvidenceDocumentId).NotEqual(Guid.Empty);
    }
}
public sealed class ResolveConcernValidator : AbstractValidator<ResolveConcernInput>
{
    public ResolveConcernValidator()
    {
        RuleFor(x => x.Outcome).NotNull().IsInEnum();
        RuleFor(x => x.Rationale).NotEmpty().MaximumLength(4000);
    }
}

// Risk Assessment & Mitigation v1. Run on the trimmed input (instants already normalized to UTC). The finding set, the note rules and the Negligible/InsufficientInformation contradiction live in
// RiskAssessmentRules (pure); the future-time rule needs the clock, so RiskAssessmentService applies it with the same 400 shape. Values are never echoed in messages.
public sealed class CreateRiskAssessmentValidator : AbstractValidator<CreateRiskAssessmentInput>
{
    public CreateRiskAssessmentValidator()
    {
        RuleFor(x => x.ReviewId).NotNull().NotEqual(Guid.Empty).WithMessage("reviewId is the id of the latest Owner review the assessment rests on.");
        RuleFor(x => x.ExpectedLatestRiskAssessmentId).NotEqual(Guid.Empty);
        RuleFor(x => x.ExpectedLatestMitigationMeasureId).NotEqual(Guid.Empty);
        RuleFor(x => x.CriteriaCatalogueVersion).NotEmpty().MaximumLength(50);
        RuleFor(x => x.Conclusion).NotNull().IsInEnum();
        RuleFor(x => x.Rationale).NotEmpty().MaximumLength(4000);
    }
}
public sealed class RecordRiskMitigationValidator : AbstractValidator<RecordRiskMitigationInput>
{
    public RecordRiskMitigationValidator()
    {
        RuleFor(x => x.ExpectedRiskAssessmentId).NotNull().NotEqual(Guid.Empty).WithMessage("expectedRiskAssessmentId is the id of the latest risk assessment the measure answers.");
        RuleFor(x => x.Description).NotEmpty().MaximumLength(4000);
        RuleFor(x => x.PerformedAtUtc).NotNull().WithMessage("performedAtUtc is the time the measure was performed.");
        RuleFor(x => x.EvidenceDocumentId).NotEqual(Guid.Empty);
    }
}
