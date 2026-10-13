using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Common;
using Provena.Api.Contracts;
using Provena.Api.Data;
using Provena.Api.Domain;
namespace Provena.Api.Services;
// Case-owned master-data copy with explicit refresh (ADR 0008). The copy holds the operator, supplier and product values a case relies on;
// shared catalogue edits never change it. SupplierId/ProductId come from the case's CURRENT shipment under the case lock. Drift (live vs copy)
// is advisory and computed on read; it never mutates the case, refreshes automatically or creates a version. Audit text carries identifiers only.
public sealed class CaseMasterDataService(AppDbContext db, TenantContext tenant, CaseService cases, AuditService audit)
{
    // 204 (null) when the case has no copy yet; 404 for an unknown or foreign case.
    public async Task<CaseMasterDataDto?> Current(Guid caseId, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, ct) ?? throw ApiException.NotFound();
        var copy = await db.CaseMasterDataVersions.AsNoTracking().Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        return copy == null ? null : await View(c, copy, ct);
    }

    // Retained copies, newest first. Unpaginated: per-case, pilot scale.
    public async Task<List<CaseMasterDataVersionDto>> History(Guid caseId, CancellationToken ct)
    {
        if (!await db.Cases.AnyAsync(x => x.Id == caseId, ct)) throw ApiException.NotFound();
        return (await db.CaseMasterDataVersions.AsNoTracking().Where(x => x.EudrCaseId == caseId).OrderByDescending(x => x.Number).ToListAsync(ct)).Select(x => x.ToDto()).ToList();
    }

    public async Task<CaseMasterDataDto> Refresh(Guid caseId, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var c = await cases.Lock(caseId, ct); CaseService.EnsureEditable(c);
        // The shipment is read under the case lock (shipment edits take the same lock), then the live sources are locked in a fixed order
        // (profile, supplier, product) FOR SHARE so a concurrent catalogue edit waits for this transaction or is already visible.
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var profile = await db.OperatorProfiles.FromSqlInterpolated($"SELECT * FROM \"OperatorProfiles\" WHERE \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleOrDefaultAsync(ct);
        var supplier = await db.Suppliers.FromSqlInterpolated($"SELECT * FROM \"Suppliers\" WHERE \"Id\" = {shipment.SupplierId} AND \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleAsync(ct);
        var product = await db.Products.FromSqlInterpolated($"SELECT * FROM \"Products\" WHERE \"Id\" = {shipment.ProductId} AND \"OrganizationId\" = {tenant.OrganizationId} FOR SHARE").AsNoTracking().SingleAsync(ct);

        var candidate = Copy(tenant.OrganizationId, caseId, profile, supplier, product);
        var current = await db.CaseMasterDataVersions.Current().SingleOrDefaultAsync(x => x.EudrCaseId == caseId, ct);
        // Identical in every copied field (context fields included): no new version, no audit event.
        if (current != null && IsIdentical(candidate, current)) { await tx.CommitAsync(ct); return await View(c, current, ct); }

        var now = DateTimeOffset.UtcNow;
        if (current != null)
        {
            // Supersede first and save before inserting the successor: EF cannot order writes around the filtered unique index.
            current.SupersededAtUtc = now;
            await db.SaveChangesAsync(ct);
        }
        candidate.Number = (await db.CaseMasterDataVersions.Where(x => x.EudrCaseId == caseId).MaxAsync(x => (int?)x.Number, ct) ?? 0) + 1;
        candidate.CreatedByUserId = tenant.UserId;
        db.CaseMasterDataVersions.Add(candidate);
        audit.Add(caseId, "CaseMasterDataRefreshed", $"Case master data version {candidate.Id} (number {candidate.Number}) copied" + (current == null ? "." : $"; superseded {current.Id}."));
        c.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await View(c, candidate, ct);
    }

    private async Task<CaseMasterDataDto> View(EudrCase c, CaseMasterDataVersion copy, CancellationToken ct)
    {
        var shipment = await db.Shipments.AsNoTracking().SingleAsync(x => x.Id == c.ShipmentId, ct);
        var profile = await db.OperatorProfiles.AsNoTracking().SingleOrDefaultAsync(ct);
        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == copy.SupplierId, ct);
        var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == copy.ProductId, ct);
        return new(copy.ToDto(), ShipmentMatches(copy, shipment), Drift(copy, profile, supplier, product));
    }

    // The copy must describe the case's CURRENT shipment: a shipment may change supplier/product while the case is still editable and has no dossier.
    public static bool ShipmentMatches(CaseMasterDataVersion copy, Shipment shipment) => copy.SupplierId == shipment.SupplierId && copy.ProductId == shipment.ProductId;

    // Builds an unsaved copy of the live values. Operator values are null when there is no profile.
    public static CaseMasterDataVersion Copy(Guid organizationId, Guid caseId, OperatorProfile? profile, Supplier supplier, Product product) => new()
    {
        OrganizationId = organizationId, EudrCaseId = caseId,
        OperatorLegalName = profile?.LegalName, OperatorPostalAddress = profile?.PostalAddress, OperatorEmail = profile?.Email,
        OperatorEstablishmentCountryCode = profile?.EstablishmentCountryCode, OperatorEori = profile?.EoriNumber,
        SupplierId = supplier.Id, SupplierName = supplier.Name, SupplierPostalAddress = supplier.PostalAddress, SupplierEmail = supplier.Email, SupplierCountryCode = supplier.CountryCode,
        ProductId = product.Id, ProductCnCode = product.CnCode, ProductCommodity = product.CommodityType, ProductName = product.Name, ProductCatalogueDescription = product.Description,
    };

    // Equality of every copied field, context fields included (refresh no-op detection).
    public static bool IsIdentical(CaseMasterDataVersion a, CaseMasterDataVersion b) =>
        a.OperatorLegalName == b.OperatorLegalName && a.OperatorPostalAddress == b.OperatorPostalAddress && a.OperatorEmail == b.OperatorEmail
        && a.OperatorEstablishmentCountryCode == b.OperatorEstablishmentCountryCode && a.OperatorEori == b.OperatorEori
        && a.SupplierId == b.SupplierId && a.SupplierName == b.SupplierName && a.SupplierPostalAddress == b.SupplierPostalAddress && a.SupplierEmail == b.SupplierEmail && a.SupplierCountryCode == b.SupplierCountryCode
        && a.ProductId == b.ProductId && a.ProductCnCode == b.ProductCnCode && a.ProductCommodity == b.ProductCommodity && a.ProductName == b.ProductName && a.ProductCatalogueDescription == b.ProductCatalogueDescription;

    // Compares the copy with the live operator profile and the copy's own supplier and product. Only fact-bearing fields take part:
    // ProductName and ProductCatalogueDescription are context, and timestamps are never used as truth.
    public static List<MasterDataDriftDto> Drift(CaseMasterDataVersion copy, OperatorProfile? profile, Supplier? supplier, Product? product)
    {
        var drift = new List<MasterDataDriftDto>();
        void Add(MasterDataArea area, params (string Field, bool Same)[] checks)
        {
            var fields = checks.Where(x => !x.Same).Select(x => x.Field).ToList();
            if (fields.Count > 0) drift.Add(new(area, fields));
        }
        Add(MasterDataArea.Operator, ("legalName", copy.OperatorLegalName == profile?.LegalName), ("postalAddress", copy.OperatorPostalAddress == profile?.PostalAddress),
            ("email", copy.OperatorEmail == profile?.Email), ("establishmentCountryCode", copy.OperatorEstablishmentCountryCode == profile?.EstablishmentCountryCode), ("eori", copy.OperatorEori == profile?.EoriNumber));
        if (supplier != null)
            Add(MasterDataArea.Supplier, ("name", copy.SupplierName == supplier.Name), ("postalAddress", copy.SupplierPostalAddress == supplier.PostalAddress),
                ("email", copy.SupplierEmail == supplier.Email), ("countryCode", copy.SupplierCountryCode == supplier.CountryCode));
        if (product != null)
            Add(MasterDataArea.Product, ("cnCode", copy.ProductCnCode == product.CnCode), ("commodity", copy.ProductCommodity == product.CommodityType));
        return drift;
    }
}
