using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
using Provena.Api.Infrastructure;
namespace Provena.Api.Services;
public sealed class SupplierRequestService(AppDbContext db, TenantContext tenant, SupplierTokenService tokens, CaseService cases,
    EudrCaseStatusService statuses, AuditService audit, IEmailSender email, IConfiguration config, IValidator<SubmissionInput> validator, ILogger<SupplierRequestService> logger)
{
    public async Task<RequestLink> Create(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct); await cases.EnsureMutable(c, ct);
        if (await db.SupplierRequests.AnyAsync(x => x.EudrCaseId == caseId && x.RevokedAtUtc == null, ct))
            throw ApiException.Conflict("A request already exists. Regenerate or revoke it first.");
        var shipment = await db.Shipments.SingleAsync(x => x.Id == c.ShipmentId, ct);
        var token = tokens.Generate(); var r = new SupplierRequest { OrganizationId = tenant.OrganizationId, EudrCaseId = c.Id, SupplierId = shipment.SupplierId, TokenHash = tokens.Hash(token), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(config.GetValue("SupplierRequests:ExpiryDays", 30)), SentAtUtc = DateTimeOffset.UtcNow };
        db.SupplierRequests.Add(r); audit.Add(c.Id, "SupplierRequestCreated", "Public request generated; link delivery is manual when using the development email sender.");
        await db.SaveChangesAsync(ct); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        var link = Link(r, token); var supplier = await db.Suppliers.SingleAsync(x => x.Id == r.SupplierId, ct);
        await email.SendSupplierRequestAsync(supplier.Email, new Uri(link.PublicUrl), ct); return link;
    }
    public async Task Revoke(Guid id, CancellationToken ct)
    {
        var r = await db.SupplierRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(r.EudrCaseId, ct);
        await db.Entry(r).ReloadAsync(ct); r.RevokedAtUtc ??= DateTimeOffset.UtcNow;
        audit.Add(c.Id, "SupplierRequestRevoked", "Supplier request revoked."); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task<RequestLink> Regenerate(Guid id, CancellationToken ct)
    {
        var r = await db.SupplierRequests.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw ApiException.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct); var c = await cases.Lock(r.EudrCaseId, ct); await cases.EnsureMutable(c, ct);
        await db.Entry(r).ReloadAsync(ct);
        var other = await db.SupplierRequests.AnyAsync(x => x.EudrCaseId == c.Id && x.Id != id && x.RevokedAtUtc == null, ct);
        if (other) throw ApiException.Conflict("A newer request already exists.");
        var token = tokens.Generate(); r.TokenHash = tokens.Hash(token); r.RevokedAtUtc = null; r.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(config.GetValue("SupplierRequests:ExpiryDays", 30)); r.SentAtUtc = DateTimeOffset.UtcNow; r.OpenedAtUtc = null; r.CompletedAtUtc = null;
        audit.Add(c.Id, "SupplierRequestRegenerated", "Previous token invalidated and a new link generated.");
        await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Link(r, token);
    }
    private RequestLink Link(SupplierRequest r, string token) => new(r.Id, $"{config["Frontend:BaseUrl"]!.TrimEnd('/')}/supplier-request/{token}", r.ExpiresAtUtc);
    public async Task<SupplierRequest> Resolve(string token, CancellationToken ct)
    {
        // The only cross-tenant business lookup. No record is exposed until the secret token is verified.
        SupplierRequest? r = null;
        if (tokens.IsWellFormed(token))
        { var hash = tokens.Hash(token); r = await db.SupplierRequests.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct); }
        if (r == null || r.RevokedAtUtc != null || r.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        { logger.LogWarning("Invalid or unavailable supplier request token attempt"); throw ApiException.NotFound(); }
        tenant.SetOrganization(r.OrganizationId); return r;
    }
    // Recheck after locking: a concurrent revoke/regenerate/expiry must invalidate a waiting request.
    public async Task<(SupplierRequest Request, EudrCase Case)> LockPublic(string token, CancellationToken ct)
    {
        var resolved = await Resolve(token, ct); var c = await cases.Lock(resolved.EudrCaseId, ct); await cases.EnsureMutable(c, ct);
        var r = await db.SupplierRequests.SingleAsync(x => x.Id == resolved.Id, ct);
        if (r.TokenHash != tokens.Hash(token) || r.RevokedAtUtc != null || r.ExpiresAtUtc <= DateTimeOffset.UtcNow) throw ApiException.NotFound();
        return (r, c);
    }
    public async Task<object> PublicDetails(string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (r, c) = await LockPublic(token, ct);
        if (r.OpenedAtUtc == null) { r.OpenedAtUtc = DateTimeOffset.UtcNow; audit.Add(c.Id, "SupplierRequestOpened", "Supplier opened the request.", r.Id); await db.SaveChangesAsync(ct); }
        var shipment = await db.Shipments.SingleAsync(x => x.Id == c.ShipmentId, ct);
        var product = await db.Products.SingleAsync(x => x.Id == shipment.ProductId, ct);
        var org = await db.Organizations.SingleAsync(x => x.Id == r.OrganizationId, ct);
        var requirements = await db.Requirements.Where(x => x.EudrCaseId == c.Id).OrderBy(x => x.Type)
            .Select(x => new { x.Id, x.Type, x.Title, x.IsRequired, x.Status }).ToListAsync(ct);
        var submission = await db.Submissions.Current().Where(x => x.EudrCaseId == c.Id).Select(x => new { x.CountryOfProductionCode, x.ProductionPeriodFrom, x.ProductionPeriodTo, x.ConfirmedQuantity, x.Notes }).SingleOrDefaultAsync(ct);
        var docs = await db.Documents.Current().Where(x => x.EudrCaseId == c.Id && x.SupplierRequestId == r.Id).Select(x => new { x.Id, x.OriginalFileName, x.SizeBytes, x.UploadedAtUtc }).ToListAsync(ct);
        var geo = await db.Geolocations.Current().Where(x => x.EudrCaseId == c.Id && x.SupplierRequestId == r.Id).Select(x => new { x.OriginalFileName, x.FeatureCount, x.GeometryTypes, x.ValidationStatus }).SingleOrDefaultAsync(ct);
        await tx.CommitAsync(ct);
        return new { requestId = r.Id, requestingOrganization = org.Name, shipmentReference = shipment.ReferenceNumber, product = new { product.Name, product.CnCode }, shipment.Quantity, shipment.Unit, requirements, supplierSubmission = submission, documents = docs, geolocation = geo, r.ExpiresAtUtc, r.CompletedAtUtc };
    }
    public async Task SaveDetails(string token, SubmissionInput input, CancellationToken ct)
    {
        await validator.ValidateAndThrowAsync(input, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (r, c) = await LockPublic(token, ct);
        // Append-only: retire the current version, then insert the new one. The retirement is saved first so the partial unique index (one current row per case) is never violated.
        var previous = await db.Submissions.Current().SingleOrDefaultAsync(x => x.EudrCaseId == c.Id, ct);
        if (previous != null) { previous.SupersededAtUtc = DateTimeOffset.UtcNow; await db.SaveChangesAsync(ct); }
        var s = new SupplierSubmission
        {
            OrganizationId = r.OrganizationId, EudrCaseId = c.Id, SupplierRequestId = r.Id, CountryOfProductionCode = input.CountryOfProductionCode,
            ProductionPeriodFrom = input.ProductionPeriodFrom, ProductionPeriodTo = input.ProductionPeriodTo, ConfirmedQuantity = input.ConfirmedQuantity, Notes = input.Notes, SubmittedAtUtc = DateTimeOffset.UtcNow
        };
        db.Submissions.Add(s);
        await SetRequirement(c.Id, RequirementType.SupplierInformation, true, ct);
        await SetRequirement(c.Id, RequirementType.CountryOfProduction, !string.IsNullOrEmpty(input.CountryOfProductionCode), ct);
        await SetRequirement(c.Id, RequirementType.ProductionPeriod, input.ProductionPeriodFrom != null && input.ProductionPeriodTo != null, ct);
        await SetRequirement(c.Id, RequirementType.QuantityConfirmation, input.ConfirmedQuantity > 0, ct);
        r.CompletedAtUtc = null; c.SupplierSubmissionCompletedAtUtc = null;
        audit.Add(c.Id, "SupplierDetailsUpdated", $"Supplier production details updated (submission {s.Id}).", r.Id); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async Task SetRequirement(Guid caseId, RequirementType type, bool present, CancellationToken ct)
    {
        var requirement = await db.Requirements.SingleAsync(x => x.EudrCaseId == caseId && x.Type == type, ct);
        if (requirement.Status == RequirementStatus.NotRequired) return;
        requirement.Status = present ? RequirementStatus.Submitted : RequirementStatus.Missing;
        requirement.ReviewerNote = null; requirement.ReviewedAtUtc = null; requirement.ReviewedByUserId = null;
    }
    public async Task Complete(string token, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct); var (r, c) = await LockPublic(token, ct);
        var missing = await db.Requirements.Where(x => x.EudrCaseId == c.Id && x.IsRequired && (x.Status == RequirementStatus.Missing || x.Status == RequirementStatus.Rejected)).Select(x => x.Type).ToListAsync(ct);
        if (missing.Count != 0) throw new ApiException(409, "Required information is missing or rejected.", new { missingRequirements = missing });
        r.CompletedAtUtc = DateTimeOffset.UtcNow; c.SupplierSubmissionCompletedAtUtc = r.CompletedAtUtc;
        audit.Add(c.Id, "SupplierSubmissionCompleted", "Supplier marked the submission complete.", r.Id); await statuses.Recalculate(c, ct); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
