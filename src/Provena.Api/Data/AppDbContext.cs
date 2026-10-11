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
        b.Entity<SupplierSubmission>().HasIndex(x => x.EudrCaseId).IsUnique();
        b.Entity<SupplierSubmission>().Property(x => x.ConfirmedQuantity).HasPrecision(20, 6);
        b.Entity<GeoSubmission>().HasIndex(x => x.EudrCaseId).IsUnique();
        b.Entity<GeoSubmission>().Property(x => x.RawGeoJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>().Property(x => x.MetadataJson).HasColumnType("jsonb");
        b.Entity<AuditEvent>().HasIndex(x => new { x.EudrCaseId, x.CreatedAtUtc });
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
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit events are append-only.");
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is TenantEntity owned && (TenantId == Guid.Empty || owned.OrganizationId != TenantId ||
                (entry.State != EntityState.Added && entry.OriginalValues.GetValue<Guid>(nameof(TenantEntity.OrganizationId)) != TenantId)))
                throw new InvalidOperationException("Organization boundary violation.");
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
