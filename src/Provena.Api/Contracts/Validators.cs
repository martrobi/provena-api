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
        RuleFor(x => x.ContactPerson).MaximumLength(200); RuleFor(x => x.CountryCode).Matches("^[A-Z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryCode));
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
        RuleFor(x => x.ConfirmedQuantity).GreaterThan(0).LessThan(100000000000000m).When(x => x.ConfirmedQuantity.HasValue);
        RuleFor(x => x).Must(x => x.ProductionPeriodFrom == null || x.ProductionPeriodTo == null || x.ProductionPeriodFrom <= x.ProductionPeriodTo).WithMessage("Production period from must be on or before production period to.");
        RuleFor(x => x.Notes).MaximumLength(4000);
    }
}
public sealed class ReviewValidator : AbstractValidator<ReviewInput>
{
    public ReviewValidator() { RuleFor(x => x.Status).Must(x => x is RequirementStatus.Accepted or RequirementStatus.Rejected or RequirementStatus.NotRequired); RuleFor(x => x.ReviewerNote).MaximumLength(4000); }
}
public sealed class CompleteValidator : AbstractValidator<CompleteInput>
{
    public CompleteValidator() { RuleFor(x => x.ExternalDdsReference).MaximumLength(200); }
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
