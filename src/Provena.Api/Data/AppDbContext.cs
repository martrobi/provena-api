using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Provena.Api.Auth;
using Provena.Api.Domain;

namespace Provena.Api.Data;
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, TenantContext tenant)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public Guid TenantId => tenant.OrganizationId;
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationUser> OrganizationUsers => Set<OrganizationUser>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<EudrCase> Cases => Set<EudrCase>();
    public DbSet<EudrRequirement> Requirements => Set<EudrRequirement>();
    public DbSet<SupplierRequest> SupplierRequests => Set<SupplierRequest>();
    public DbSet<SupplierSubmission> Submissions => Set<SupplierSubmission>();
    public DbSet<CaseDocument> Documents => Set<CaseDocument>();
    public DbSet<GeoSubmission> Geolocations => Set<GeoSubmission>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Producer> Producers => Set<Producer>();
    public DbSet<ProductionPlot> ProductionPlots => Set<ProductionPlot>();
    public DbSet<PlotRevision> PlotRevisions => Set<PlotRevision>();
    public DbSet<CaseDossier> CaseDossiers => Set<CaseDossier>();
    public DbSet<CaseLot> CaseLots => Set<CaseLot>();
    public DbSet<LotContribution> LotContributions => Set<LotContribution>();
    public DbSet<ContributionPlot> ContributionPlots => Set<ContributionPlot>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<ApplicationUser>().Property(x => x.FirstName).HasMaxLength(100);
        b.Entity<ApplicationUser>().Property(x => x.LastName).HasMaxLength(100);
        b.Entity<ApplicationUser>().HasIndex(x => x.NormalizedEmail).IsUnique();
        b.Entity<Organization>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<Organization>().Property(x => x.Oib).HasMaxLength(11);
        b.Entity<OrganizationUser>().HasKey(x => new { x.OrganizationId, x.UserId });
        b.Entity<OrganizationUser>().HasIndex(x => x.UserId).IsUnique(); // One organization per account in MVP.
        b.Entity<OrganizationUser>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrganizationUser>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        ConfigureTenant<Supplier>(b); ConfigureTenant<Product>(b); ConfigureTenant<Shipment>(b);
        ConfigureTenant<EudrCase>(b); ConfigureTenant<EudrRequirement>(b); ConfigureTenant<SupplierRequest>(b);
        ConfigureTenant<SupplierSubmission>(b); ConfigureTenant<CaseDocument>(b); ConfigureTenant<GeoSubmission>(b); ConfigureTenant<AuditEvent>(b);
        ConfigureTenant<Producer>(b); ConfigureTenant<ProductionPlot>(b); ConfigureTenant<PlotRevision>(b);
        ConfigureTenant<CaseDossier>(b); ConfigureTenant<CaseLot>(b); ConfigureTenant<LotContribution>(b); ConfigureTenant<ContributionPlot>(b);
        b.Entity<Supplier>().HasIndex(x => new { x.OrganizationId, x.ExternalReference }).IsUnique().HasFilter("\"ExternalReference\" IS NOT NULL");
        b.Entity<Product>().HasIndex(x => new { x.OrganizationId, x.ExternalReference }).IsUnique().HasFilter("\"ExternalReference\" IS NOT NULL");
        b.Entity<Shipment>().HasIndex(x => new { x.OrganizationId, x.ReferenceNumber }).IsUnique();
        b.Entity<Shipment>().HasIndex(x => new { x.OrganizationId, x.SupplierId });
        b.Entity<Shipment>().HasIndex(x => new { x.OrganizationId, x.ProductId });
        b.Entity<Shipment>().Property(x => x.Quantity).HasPrecision(20, 6);
        b.Entity<Shipment>().HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Shipment>().HasOne<Product>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EudrCase>().HasIndex(x => x.ShipmentId).IsUnique();
        b.Entity<EudrCase>().HasIndex(x => new { x.OrganizationId, x.Status });
        b.Entity<EudrCase>().HasOne<Shipment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ShipmentId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<EudrRequirement>().HasIndex(x => new { x.EudrCaseId, x.Type }).IsUnique();
        CaseForeignKey<EudrRequirement>(b); CaseForeignKey<SupplierRequest>(b); CaseForeignKey<SupplierSubmission>(b);
        CaseForeignKey<CaseDocument>(b); CaseForeignKey<GeoSubmission>(b);
        b.Entity<AuditEvent>().HasOne<EudrCase>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SupplierRequest>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<SupplierRequest>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"RevokedAtUtc\" IS NULL");
        b.Entity<SupplierRequest>().Property(x => x.TokenHash).HasMaxLength(64);
        b.Entity<SupplierRequest>().HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<SupplierSubmission>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current version per case.
        b.Entity<SupplierSubmission>().Property(x => x.ConfirmedQuantity).HasPrecision(20, 6);
        b.Entity<GeoSubmission>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current version per case.
        b.Entity<GeoSubmission>().Property(x => x.RawGeoJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>().Property(x => x.MetadataJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>().HasIndex(x => new { x.EudrCaseId, x.CreatedAtUtc });
        b.Entity<Producer>().HasIndex(x => new { x.OrganizationId, x.ExternalReference }).IsUnique().HasFilter("\"ExternalReference\" IS NOT NULL");
        b.Entity<ProductionPlot>().HasIndex(x => new { x.OrganizationId, x.ExternalReference }).IsUnique().HasFilter("\"ExternalReference\" IS NOT NULL");
        b.Entity<ProductionPlot>().HasOne<Producer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProducerId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlotRevision>().HasOne<ProductionPlot>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionPlotId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlotRevision>().HasIndex(x => new { x.ProductionPlotId, x.Number }).IsUnique();
        b.Entity<PlotRevision>().HasIndex(x => x.ProductionPlotId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current revision per plot.
        b.Entity<PlotRevision>().Property(x => x.GeometryJson).HasColumnType("jsonb");
        b.Entity<PlotRevision>().Property(x => x.AreaHectares).HasPrecision(12, 4);
        // Alternate keys that let the case dossier's composite foreign keys enforce producer -> plot -> revision consistency in the database.
        b.Entity<ProductionPlot>().HasAlternateKey(x => new { x.OrganizationId, x.ProducerId, x.Id });
        b.Entity<PlotRevision>().HasAlternateKey(x => new { x.OrganizationId, x.ProductionPlotId, x.Id });
        // EF would otherwise drop these FK-supporting indexes as redundant with the new keys; keep them so the migration stays purely additive.
        b.Entity<ProductionPlot>().HasIndex(x => new { x.OrganizationId, x.ProducerId });
        b.Entity<PlotRevision>().HasIndex(x => new { x.OrganizationId, x.ProductionPlotId });
        CaseForeignKey<CaseDossier>(b);
        b.Entity<CaseDossier>().HasIndex(x => x.EudrCaseId).IsUnique(); // At most one dossier per case.
        b.Entity<CaseLot>().HasOne<CaseDossier>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CaseDossierId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseLot>().HasIndex(x => new { x.CaseDossierId, x.Number }).IsUnique();
        b.Entity<CaseLot>().HasIndex(x => x.CaseDossierId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current lot per dossier.
        b.Entity<CaseLot>().HasIndex(x => new { x.OrganizationId, x.LotReference }).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // Versioned reference: unique among current lots only.
        b.Entity<CaseLot>().Property(x => x.LotReference).HasMaxLength(100);
        b.Entity<CaseLot>().Property(x => x.Quantity).HasPrecision(20, 6);
        b.Entity<LotContribution>().HasAlternateKey(x => new { x.OrganizationId, x.Id, x.ProducerId });
        b.Entity<LotContribution>().HasOne<CaseLot>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CaseLotId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LotContribution>().HasOne<Producer>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProducerId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<LotContribution>().HasIndex(x => new { x.CaseLotId, x.ProducerId }).IsUnique();
        b.Entity<LotContribution>().Property(x => x.Quantity).HasPrecision(20, 6);
        b.Entity<ContributionPlot>().HasOne<LotContribution>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.LotContributionId, x.ProducerId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id, x.ProducerId }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ContributionPlot>().HasOne<ProductionPlot>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProducerId, x.ProductionPlotId }).HasPrincipalKey(x => new { x.OrganizationId, x.ProducerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ContributionPlot>().HasOne<PlotRevision>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductionPlotId, x.PlotRevisionId }).HasPrincipalKey(x => new { x.OrganizationId, x.ProductionPlotId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ContributionPlot>().HasIndex(x => new { x.LotContributionId, x.ProductionPlotId }).IsUnique();
        foreach (var type in b.Model.GetEntityTypes())
        foreach (var property in type.GetProperties().Where(p => p.ClrType == typeof(string) && p.GetMaxLength() == null && p.GetColumnType() != "jsonb"))
            property.SetMaxLength(4000);
    }
    private void ConfigureTenant<T>(ModelBuilder b) where T : TenantEntity
    {
        b.Entity<T>().HasBaseType((Type?)null);
        b.Entity<T>().HasKey(x => x.Id);
        b.Entity<T>().HasAlternateKey(x => new { x.OrganizationId, x.Id });
        b.Entity<T>().HasQueryFilter(x => x.OrganizationId == TenantId);
        b.Entity<T>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
    private static void CaseForeignKey<T>(ModelBuilder b) where T : TenantEntity => b.Entity<T>()
        .HasOne<EudrCase>().WithMany().HasForeignKey("OrganizationId", "EudrCaseId")
        .HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    // Retained evidence rows are append-only: never deleted, and the only allowed update is the one-way retirement (null -> value).
    // Application-level only: other SaveChanges overloads, bulk writes and raw SQL bypass this guard.
    // Marker = the property whose null -> value change defines the retirement; companions may only change in that same transition.
    private static readonly Dictionary<Type, (string Marker, string[] Companions)> Retirement = new()
    {
        [typeof(CaseDocument)] = (nameof(CaseDocument.WithdrawnAtUtc), [nameof(CaseDocument.WithdrawnByUserId)]),
        [typeof(GeoSubmission)] = (nameof(GeoSubmission.SupersededAtUtc), []),
        [typeof(SupplierSubmission)] = (nameof(SupplierSubmission.SupersededAtUtc), []),
        [typeof(PlotRevision)] = (nameof(PlotRevision.SupersededAtUtc), []),
        [typeof(CaseLot)] = (nameof(CaseLot.SupersededAtUtc), []),
    };
    private static void EnforceRetainedEvidence(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State == EntityState.Deleted) throw new InvalidOperationException("Retained evidence is never deleted; withdraw or supersede it.");
        if (entry.State != EntityState.Modified) return;
        var (marker, companions) = Retirement[entry.Entity.GetType()];
        // UpdatedAtUtc alone is not a retirement; it is only tolerated alongside one.
        var changed = entry.Properties.Where(p => p.IsModified && p.Metadata.Name != nameof(Entity.UpdatedAtUtc)).Select(p => p.Metadata.Name).ToList();
        var fromUnretired = entry.Property(marker).OriginalValue == null && companions.All(c => entry.Property(c).OriginalValue == null);
        if (!changed.Contains(marker) || !fromUnretired || changed.Any(n => n != marker && !companions.Contains(n)))
            throw new InvalidOperationException("Retained evidence is immutable except for one-way retirement.");
    }
    // Provenance identities are never deleted (deactivate instead); a plot never changes producer.
    private static void EnforceProvenanceIdentity(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State == EntityState.Deleted) throw new InvalidOperationException("Provenance identities are never deleted; deactivate them.");
        if (entry.State == EntityState.Modified && entry.Entity is ProductionPlot && entry.Property(nameof(ProductionPlot.ProducerId)).IsModified)
            throw new InvalidOperationException("A production plot's producer is immutable; register a new plot instead.");
    }
    // Case dossier anchors, lot contributions and plot links are fully immutable and never deleted (new facts = a new CaseLot version).
    // Application-level only, like the guards above.
    private static void EnforceDossierImmutable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State is EntityState.Modified or EntityState.Deleted)
            throw new InvalidOperationException("Case dossier rows are immutable; save a new lot version.");
    }
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit events are append-only.");
            if (entry.Entity is CaseDocument or GeoSubmission or SupplierSubmission or PlotRevision or CaseLot) EnforceRetainedEvidence(entry);
            if (entry.Entity is Producer or ProductionPlot) EnforceProvenanceIdentity(entry);
            if (entry.Entity is CaseDossier or LotContribution or ContributionPlot) EnforceDossierImmutable(entry);
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is TenantEntity owned && (TenantId == Guid.Empty || owned.OrganizationId != TenantId ||
                (entry.State != EntityState.Added && entry.OriginalValues.GetValue<Guid>(nameof(TenantEntity.OrganizationId)) != TenantId)))
                throw new InvalidOperationException("Organization boundary violation.");
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
