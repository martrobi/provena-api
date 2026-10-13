using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// The organization's single operator profile (ADR 0008). PUT is an upsert; there is no delete. The profile is only a SOURCE for the case
// master-data copy: saving it never changes any case. EORI is stored, not evaluated. Audit text carries identifiers only (no personal or address data).
public sealed class OperatorProfileService(AppDbContext db, TenantContext tenant, AuditService audit, IValidator<OperatorProfileInput> validator)
{
    // 204 (null) when the organization has no profile yet.
    public async Task<OperatorProfileDto?> Get(CancellationToken ct) => (await db.OperatorProfiles.AsNoTracking().SingleOrDefaultAsync(ct))?.ToDto();

    public async Task<OperatorProfileDto> Save(OperatorProfileInput input, CancellationToken ct)
    {
        var normalized = Normalize(input);
        await validator.ValidateAndThrowAsync(normalized, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The profile row is locked for the write (refresh locks it FOR SHARE). A concurrent first insert is stopped by the unique organization index (409).
        var profile = await db.OperatorProfiles.FromSqlInterpolated($"SELECT * FROM \"OperatorProfiles\" WHERE \"OrganizationId\" = {tenant.OrganizationId} FOR UPDATE").SingleOrDefaultAsync(ct);
        var created = profile == null;
        if (profile == null) { profile = new OperatorProfile { OrganizationId = tenant.OrganizationId }; db.OperatorProfiles.Add(profile); }
        profile.LegalName = normalized.LegalName; profile.PostalAddress = normalized.PostalAddress; profile.Email = normalized.Email;
        profile.EstablishmentCountryCode = normalized.EstablishmentCountryCode; profile.EoriNumber = normalized.EoriNumber; profile.UpdatedByUserId = tenant.UserId;
        audit.Add(null, "OperatorProfileSaved", $"Operator profile {profile.Id} " + (created ? "created." : "updated."));
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return profile.ToDto();
    }

    // Trims text and turns a blank EORI into null; tolerates null members so validation (not a NullReferenceException) reports them.
    public static OperatorProfileInput Normalize(OperatorProfileInput input) => input with
    {
        LegalName = (input.LegalName ?? "").Trim(), PostalAddress = (input.PostalAddress ?? "").Trim(), Email = (input.Email ?? "").Trim(),
        EstablishmentCountryCode = (input.EstablishmentCountryCode ?? "").Trim(), EoriNumber = string.IsNullOrWhiteSpace(input.EoriNumber) ? null : input.EoriNumber.Trim(),
    };
}
