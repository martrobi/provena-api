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
    public DbSet<OperatorProfile> OperatorProfiles => Set<OperatorProfile>();
    public DbSet<CaseMasterDataVersion> CaseMasterDataVersions => Set<CaseMasterDataVersion>();
    public DbSet<CaseApplicability> CaseApplicabilities => Set<CaseApplicability>();
    public DbSet<CaseRecipientStatement> CaseRecipientStatements => Set<CaseRecipientStatement>();
    public DbSet<CaseRecipient> CaseRecipients => Set<CaseRecipient>();
    public DbSet<CaseEvidenceLink> CaseEvidenceLinks => Set<CaseEvidenceLink>();
    public DbSet<CaseRevision> CaseRevisions => Set<CaseRevision>();
    public DbSet<CaseRevisionDocument> CaseRevisionDocuments => Set<CaseRevisionDocument>();
    public DbSet<CaseRevisionEvidenceLink> CaseRevisionEvidenceLinks => Set<CaseRevisionEvidenceLink>();
    public DbSet<ReviewRecord> ReviewRecords => Set<ReviewRecord>();
    public DbSet<CaseClosure> CaseClosures => Set<CaseClosure>();
    public DbSet<CaseReopening> CaseReopenings => Set<CaseReopening>();
    public DbSet<CaseArchival> CaseArchivals => Set<CaseArchival>();
    public DbSet<ExternalDdsRecord> ExternalDdsRecords => Set<ExternalDdsRecord>();
    public DbSet<CaseConcern> CaseConcerns => Set<CaseConcern>();
    public DbSet<ConcernAction> ConcernActions => Set<ConcernAction>();
    public DbSet<ConcernNotification> ConcernNotifications => Set<ConcernNotification>();
    public DbSet<ConcernResolution> ConcernResolutions => Set<ConcernResolution>();
    public DbSet<RiskAssessment> RiskAssessments => Set<RiskAssessment>();
    public DbSet<RiskAssessmentFinding> RiskAssessmentFindings => Set<RiskAssessmentFinding>();
    public DbSet<RiskMitigationMeasure> RiskMitigationMeasures => Set<RiskMitigationMeasure>();
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
        ConfigureTenant<OperatorProfile>(b); ConfigureTenant<CaseMasterDataVersion>(b); ConfigureTenant<CaseApplicability>(b);
        ConfigureTenant<CaseRecipientStatement>(b); ConfigureTenant<CaseRecipient>(b); ConfigureTenant<CaseEvidenceLink>(b);
        ConfigureTenant<CaseRevision>(b); ConfigureTenant<CaseRevisionDocument>(b); ConfigureTenant<CaseRevisionEvidenceLink>(b); ConfigureTenant<ReviewRecord>(b);
        ConfigureTenant<CaseClosure>(b); ConfigureTenant<CaseReopening>(b); ConfigureTenant<CaseArchival>(b); ConfigureTenant<ExternalDdsRecord>(b);
        ConfigureTenant<CaseConcern>(b); ConfigureTenant<ConcernAction>(b); ConfigureTenant<ConcernNotification>(b); ConfigureTenant<ConcernResolution>(b);
        ConfigureTenant<RiskAssessment>(b); ConfigureTenant<RiskAssessmentFinding>(b); ConfigureTenant<RiskMitigationMeasure>(b);
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
        b.Entity<ContributionPlot>().Property(x => x.AllocatedQuantity).HasPrecision(20, 6);
        b.Entity<ContributionPlot>().ToTable(t => t.HasCheckConstraint("CK_ContributionPlots_AllocatedQuantityPositive", "\"AllocatedQuantity\" IS NULL OR \"AllocatedQuantity\" > 0"));

        // Completeness facts (ADR 0008): operator profile, case master-data copy, applicability, recipients and evidence links.
        b.Entity<Supplier>().Property(x => x.PostalAddress).HasMaxLength(1000);
        b.Entity<OperatorProfile>().HasIndex(x => x.OrganizationId).IsUnique(); // One profile per organization.
        b.Entity<OperatorProfile>().Property(x => x.LegalName).HasMaxLength(200);
        b.Entity<OperatorProfile>().Property(x => x.PostalAddress).HasMaxLength(1000);
        b.Entity<OperatorProfile>().Property(x => x.Email).HasMaxLength(256);
        b.Entity<OperatorProfile>().Property(x => x.EstablishmentCountryCode).HasMaxLength(2);
        b.Entity<OperatorProfile>().Property(x => x.EoriNumber).HasMaxLength(17);

        CaseForeignKey<CaseMasterDataVersion>(b); CaseForeignKey<CaseApplicability>(b); CaseForeignKey<CaseRecipientStatement>(b); CaseForeignKey<CaseEvidenceLink>(b);
        b.Entity<CaseMasterDataVersion>().HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.SupplierId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseMasterDataVersion>().HasOne<Product>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.ProductId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseMasterDataVersion>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<CaseMasterDataVersion>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current copy per case.
        b.Entity<CaseMasterDataVersion>().Property(x => x.OperatorLegalName).HasMaxLength(200);
        b.Entity<CaseMasterDataVersion>().Property(x => x.OperatorPostalAddress).HasMaxLength(1000);
        b.Entity<CaseMasterDataVersion>().Property(x => x.OperatorEmail).HasMaxLength(256);
        b.Entity<CaseMasterDataVersion>().Property(x => x.OperatorEstablishmentCountryCode).HasMaxLength(2);
        b.Entity<CaseMasterDataVersion>().Property(x => x.OperatorEori).HasMaxLength(17);
        b.Entity<CaseMasterDataVersion>().Property(x => x.SupplierName).HasMaxLength(200);
        b.Entity<CaseMasterDataVersion>().Property(x => x.SupplierPostalAddress).HasMaxLength(1000);
        b.Entity<CaseMasterDataVersion>().Property(x => x.SupplierEmail).HasMaxLength(256);
        b.Entity<CaseMasterDataVersion>().Property(x => x.SupplierCountryCode).HasMaxLength(2);
        b.Entity<CaseMasterDataVersion>().Property(x => x.ProductCnCode).HasMaxLength(10);
        b.Entity<CaseMasterDataVersion>().Property(x => x.ProductName).HasMaxLength(200);

        b.Entity<CaseApplicability>().HasOne<Product>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.AssessedProductId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseApplicability>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<CaseApplicability>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current version per case.
        b.Entity<CaseApplicability>().Property(x => x.AssessedCnCode).HasMaxLength(10);
        b.Entity<CaseApplicability>().Property(x => x.ProductDescription).HasMaxLength(2000);
        b.Entity<CaseApplicability>().Property(x => x.OperatorRoleDescription).HasMaxLength(500);
        b.Entity<CaseApplicability>().Property(x => x.ScopeCitation).HasMaxLength(1000);
        b.Entity<CaseApplicability>().Property(x => x.Qualifications).HasMaxLength(2000);
        b.Entity<CaseApplicability>().Property(x => x.LegalBasisVersion).HasMaxLength(200);
        b.Entity<CaseApplicability>().Property(x => x.Reasoning).HasMaxLength(4000);

        b.Entity<CaseRecipientStatement>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<CaseRecipientStatement>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current statement per case.
        b.Entity<CaseRecipientStatement>().Property(x => x.NotApplicableBasis).HasMaxLength(2000);
        b.Entity<CaseRecipient>().HasOne<CaseRecipientStatement>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CaseRecipientStatementId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseRecipient>().Property(x => x.Name).HasMaxLength(200);
        b.Entity<CaseRecipient>().Property(x => x.PostalAddress).HasMaxLength(1000);
        b.Entity<CaseRecipient>().Property(x => x.Email).HasMaxLength(256);

        // Alternate key that lets an evidence link prove in the database that its document belongs to the link's own case.
        b.Entity<CaseDocument>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        // EF would otherwise drop this FK-supporting index as redundant with the new key; keep it so the migration stays purely additive.
        b.Entity<CaseDocument>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<CaseEvidenceLink>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseEvidenceLink>().HasIndex(x => new { x.EudrCaseId, x.CaseDocumentId, x.Purpose }).IsUnique().HasFilter("\"WithdrawnAtUtc\" IS NULL"); // At most one current link per document and purpose.
        b.Entity<CaseEvidenceLink>().Property(x => x.Note).HasMaxLength(1000);
        // Reviewed dossier history (ADR 0008): immutable revisions pin the reviewed rows through case-consistent composite foreign keys.
        // Alternate keys: the principal side of every pin FK. The case foreign-key indexes they cover are kept explicitly so the migration stays purely additive.
        b.Entity<CaseDossier>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseLot>().HasAlternateKey(x => new { x.OrganizationId, x.CaseDossierId, x.Id });
        b.Entity<GeoSubmission>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<SupplierSubmission>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseApplicability>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseRecipientStatement>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseEvidenceLink>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseMasterDataVersion>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseDossier>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<CaseLot>().HasIndex(x => new { x.OrganizationId, x.CaseDossierId });
        b.Entity<GeoSubmission>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<SupplierSubmission>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<CaseApplicability>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<CaseRecipientStatement>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });
        b.Entity<CaseMasterDataVersion>().HasIndex(x => new { x.OrganizationId, x.EudrCaseId });

        CaseForeignKey<CaseRevision>(b); CaseForeignKey<CaseRevisionDocument>(b); CaseForeignKey<CaseRevisionEvidenceLink>(b); CaseForeignKey<ReviewRecord>(b);
        b.Entity<CaseRevision>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseRevision>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique(); // The sequencing invariant: "latest" is the highest Number, never a timestamp.
        b.Entity<CaseRevision>().HasIndex(x => x.Fingerprint);
        b.Entity<CaseRevision>().Property(x => x.PolicyId).HasMaxLength(100);
        b.Entity<CaseRevision>().Property(x => x.PolicyVersion).HasMaxLength(50);
        b.Entity<CaseRevision>().Property(x => x.Fingerprint).HasMaxLength(64);
        b.Entity<CaseRevision>().Property(x => x.SectionHashes).HasColumnType("jsonb");
        b.Entity<CaseRevision>().Property(x => x.SnapshotJson).HasColumnType("jsonb");
        // Structural integrity only: MATCH SIMPLE would skip the dossier -> lot foreign key on a half-null pair. (That the lot is the CURRENT lot stays a service obligation.)
        b.Entity<CaseRevision>().ToTable("CaseRevisions", t => t.HasCheckConstraint("CK_CaseRevisions_DossierLotPair",
            "(\"CaseDossierId\" IS NULL AND \"CaseLotId\" IS NULL) OR (\"CaseDossierId\" IS NOT NULL AND \"CaseLotId\" IS NOT NULL)"));
        b.Entity<CaseRevision>().HasOne<CaseDossier>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseDossierId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<CaseLot>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CaseDossierId, x.CaseLotId }).HasPrincipalKey(x => new { x.OrganizationId, x.CaseDossierId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<CaseApplicability>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseApplicabilityId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<CaseRecipientStatement>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseRecipientStatementId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<CaseMasterDataVersion>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseMasterDataVersionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<GeoSubmission>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.GeoSubmissionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseRevision>().HasOne<SupplierSubmission>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.SupplierSubmissionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);

        b.Entity<CaseRevisionDocument>().HasOne<CaseRevision>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseRevisionDocument>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseRevisionDocument>().HasIndex(x => new { x.CaseRevisionId, x.CaseDocumentId }).IsUnique();
        b.Entity<CaseRevisionEvidenceLink>().HasOne<CaseRevision>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseRevisionEvidenceLink>().HasOne<CaseEvidenceLink>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseEvidenceLinkId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseRevisionEvidenceLink>().HasIndex(x => new { x.CaseRevisionId, x.CaseEvidenceLinkId }).IsUnique();

        b.Entity<ReviewRecord>().HasOne<CaseRevision>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReviewRecord>().HasIndex(x => x.CaseRevisionId).IsUnique(); // One review per revision and one revision per review.
        b.Entity<ReviewRecord>().HasIndex(x => new { x.EudrCaseId, x.ReviewedAtUtc }); // History ordering aid only: deliberately NOT unique (two reviews may share a timestamp).
        b.Entity<ReviewRecord>().Property(x => x.Rationale).HasMaxLength(4000);
        b.Entity<ReviewRecord>().Property(x => x.PolicyVersion).HasMaxLength(50);

        // Case lifecycle v2: immutable closure and reopening history plus an independent archive history. A closure is backed by exactly one review of the SAME case.
        // The review alternate key lets a closure prove in the database that its review belongs to the closure's own case and organization.
        CaseForeignKey<CaseClosure>(b); CaseForeignKey<CaseReopening>(b); CaseForeignKey<CaseArchival>(b);
        b.Entity<ReviewRecord>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseClosure>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseClosure>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique(); // The sequencing invariant: the current closure is the highest Number.
        b.Entity<CaseClosure>().HasIndex(x => x.ReviewRecordId).IsUnique(); // One review backs at most one closure.
        b.Entity<CaseClosure>().HasOne<ReviewRecord>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseClosure>().Property(x => x.ExternalDdsReference).HasMaxLength(200);
        b.Entity<CaseReopening>().HasOne<CaseClosure>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.CaseClosureId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CaseReopening>().HasIndex(x => x.CaseClosureId).IsUnique(); // A closure is reopened at most once.
        b.Entity<CaseReopening>().Property(x => x.Reason).HasMaxLength(4000);
        b.Entity<CaseArchival>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<CaseArchival>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"UnarchivedAtUtc\" IS NULL"); // At most one current archive period per case.
        b.Entity<CaseArchival>().Property(x => x.Reason).HasMaxLength(4000);
        b.Entity<CaseArchival>().Property(x => x.UnarchiveReason).HasMaxLength(4000);
        // External DDS Record v1: append-only manual external-submission facts. Review, receipt document and superseded record are case-consistent composite Restrict FKs, so the
        // database proves they belong to the record's own organization and case. Retired one way through SupersededAtUtc (retirement map).
        CaseForeignKey<ExternalDdsRecord>(b);
        b.Entity<ExternalDdsRecord>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<ExternalDdsRecord>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<ExternalDdsRecord>().HasIndex(x => x.EudrCaseId).IsUnique().HasFilter("\"SupersededAtUtc\" IS NULL"); // At most one current record per case.
        b.Entity<ExternalDdsRecord>().HasIndex(x => x.SupersedesExternalDdsRecordId).IsUnique().HasFilter("\"SupersedesExternalDdsRecordId\" IS NOT NULL"); // A record is superseded by at most one.
        b.Entity<ExternalDdsRecord>().HasIndex(x => x.ReviewId);
        b.Entity<ExternalDdsRecord>().HasOne<ReviewRecord>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ExternalDdsRecord>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReceiptDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<ExternalDdsRecord>().HasOne<ExternalDdsRecord>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.SupersedesExternalDdsRecordId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<ExternalDdsRecord>().Property(x => x.ReferenceNumber).HasMaxLength(200);
        b.Entity<ExternalDdsRecord>().Property(x => x.VerificationNumber).HasMaxLength(200);
        b.Entity<ExternalDdsRecord>().Property(x => x.ReplacementReason).HasMaxLength(4000);
        b.Entity<ExternalDdsRecord>().ToTable("ExternalDdsRecords", t =>
        {
            t.HasCheckConstraint("CK_ExternalDdsRecords_NonblankNumbers", "btrim(\"ReferenceNumber\") <> '' AND btrim(\"VerificationNumber\") <> ''");
            t.HasCheckConstraint("CK_ExternalDdsRecords_ReplacementPair", "(\"SupersedesExternalDdsRecordId\" IS NULL AND \"ReplacementReason\" IS NULL) OR (\"SupersedesExternalDdsRecordId\" IS NOT NULL AND \"ReplacementReason\" IS NOT NULL AND btrim(\"ReplacementReason\") <> '')");
        });
        // Post-completion Concern & Corrective Action v1: fully immutable retained history. Every child proves in the database that its concern (and optional evidence document) belongs to its
        // own organization and case; the recipient is the immutable CaseRecipient row (that it belongs to this case is a service rule). One resolution per concern; concern numbers are unique per case.
        CaseForeignKey<CaseConcern>(b); CaseForeignKey<ConcernAction>(b); CaseForeignKey<ConcernNotification>(b); CaseForeignKey<ConcernResolution>(b);
        b.Entity<CaseConcern>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<CaseConcern>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<CaseConcern>().Property(x => x.Summary).HasMaxLength(200);
        b.Entity<CaseConcern>().Property(x => x.Details).HasMaxLength(4000);
        b.Entity<CaseConcern>().ToTable("CaseConcerns", t => t.HasCheckConstraint("CK_CaseConcerns_NonblankText", "btrim(\"Summary\") <> '' AND btrim(\"Details\") <> ''"));
        b.Entity<ConcernAction>().HasOne<CaseConcern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ConcernAction>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<ConcernAction>().HasIndex(x => x.ConcernId);
        b.Entity<ConcernAction>().Property(x => x.Description).HasMaxLength(4000);
        b.Entity<ConcernAction>().ToTable("ConcernActions", t => t.HasCheckConstraint("CK_ConcernActions_NonblankDescription", "btrim(\"Description\") <> ''"));
        b.Entity<ConcernNotification>().HasOne<CaseConcern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ConcernNotification>().HasOne<CaseRecipient>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.CaseRecipientId }).HasPrincipalKey(x => new { x.OrganizationId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ConcernNotification>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<ConcernNotification>().HasIndex(x => x.ConcernId);
        b.Entity<ConcernNotification>().Property(x => x.Note).HasMaxLength(1000);
        b.Entity<ConcernNotification>().ToTable("ConcernNotifications", t => t.HasCheckConstraint("CK_ConcernNotifications_NonblankNote", "\"Note\" IS NULL OR btrim(\"Note\") <> ''"));
        b.Entity<ConcernResolution>().HasOne<CaseConcern>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ConcernResolution>().HasIndex(x => x.ConcernId).IsUnique(); // One resolution per concern.
        b.Entity<ConcernResolution>().Property(x => x.Rationale).HasMaxLength(4000);
        b.Entity<ConcernResolution>().ToTable("ConcernResolutions", t => t.HasCheckConstraint("CK_ConcernResolutions_NonblankRationale", "btrim(\"Rationale\") <> ''"));
        // Risk Assessment & Mitigation v1: fully immutable retained decisions. An assessment pins ONE review of its own case through a case-consistent composite Restrict FK. Its second alternate key
        // (Org, Case, ReviewRecordId, Id) is the principal of the closure's FK: a CaseClosure references (Org, Case, ITS OWN ReviewRecordId, RiskAssessmentId), so the database makes it impossible for a
        // closure on review R2 to reference an assessment made on review R1 (or on another case or organization). Legacy closures and the other outcomes keep RiskAssessmentId null (MATCH SIMPLE).
        // "Latest assessment", "the review backs no closure yet" and the freshness of the review stay service obligations under the case lock.
        CaseForeignKey<RiskAssessment>(b); CaseForeignKey<RiskAssessmentFinding>(b); CaseForeignKey<RiskMitigationMeasure>(b);
        b.Entity<RiskAssessment>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id });
        b.Entity<RiskAssessment>().HasAlternateKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId, x.Id });
        b.Entity<RiskAssessment>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique(); // The sequencing invariant: "latest" is the highest Number.
        b.Entity<RiskAssessment>().HasIndex(x => x.ReviewRecordId);
        b.Entity<RiskAssessment>().HasOne<ReviewRecord>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RiskAssessment>().Property(x => x.CriteriaCatalogueId).HasMaxLength(100);
        b.Entity<RiskAssessment>().Property(x => x.CriteriaCatalogueVersion).HasMaxLength(50);
        b.Entity<RiskAssessment>().Property(x => x.Rationale).HasMaxLength(4000);
        b.Entity<RiskAssessment>().ToTable("RiskAssessments", t =>
        {
            t.HasCheckConstraint("CK_RiskAssessments_NonblankRationale", "btrim(\"Rationale\") <> ''");
            t.HasCheckConstraint("CK_RiskAssessments_MitigationNumber", "\"ConsideredMitigationThroughNumber\" IS NULL OR \"ConsideredMitigationThroughNumber\" > 0");
        });
        b.Entity<RiskAssessmentFinding>().HasOne<RiskAssessment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.RiskAssessmentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RiskAssessmentFinding>().HasIndex(x => new { x.RiskAssessmentId, x.CriterionCode }).IsUnique(); // One finding per criterion and assessment.
        b.Entity<RiskAssessmentFinding>().Property(x => x.CriterionCode).HasMaxLength(100);
        b.Entity<RiskAssessmentFinding>().Property(x => x.Note).HasMaxLength(2000);
        b.Entity<RiskAssessmentFinding>().ToTable("RiskAssessmentFindings", t =>
        {
            t.HasCheckConstraint("CK_RiskAssessmentFindings_NonblankNote", "\"Note\" IS NULL OR btrim(\"Note\") <> ''");
            t.HasCheckConstraint("CK_RiskAssessmentFindings_NoteRequired", "\"Finding\" = 0 OR \"Note\" IS NOT NULL"); // 0 = NoConcern; Concern and InsufficientInformation need a note
        });
        b.Entity<RiskMitigationMeasure>().HasOne<RiskAssessment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.RiskAssessmentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.Entity<RiskMitigationMeasure>().HasOne<CaseDocument>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<RiskMitigationMeasure>().HasIndex(x => new { x.EudrCaseId, x.Number }).IsUnique();
        b.Entity<RiskMitigationMeasure>().HasIndex(x => x.RiskAssessmentId);
        b.Entity<RiskMitigationMeasure>().Property(x => x.Description).HasMaxLength(4000);
        b.Entity<RiskMitigationMeasure>().ToTable("RiskMitigationMeasures", t => t.HasCheckConstraint("CK_RiskMitigationMeasures_NonblankDescription", "btrim(\"Description\") <> ''"));
        // The closure -> assessment consistency FK (see above) and its backstops: an assessment backs at most one closure, and only an ExternalDdsCompleted closure (Outcome 0) carries one.
        b.Entity<CaseClosure>().HasOne<RiskAssessment>().WithMany().HasForeignKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId, x.RiskAssessmentId }).HasPrincipalKey(x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId, x.Id }).OnDelete(DeleteBehavior.Restrict).IsRequired(false);
        b.Entity<CaseClosure>().HasIndex(x => x.RiskAssessmentId).IsUnique().HasFilter("\"RiskAssessmentId\" IS NOT NULL");
        b.Entity<CaseClosure>().ToTable("CaseClosures", t => t.HasCheckConstraint("CK_CaseClosures_RiskAssessmentOutcome", "\"RiskAssessmentId\" IS NULL OR \"Outcome\" = 0"));
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
        [typeof(CaseMasterDataVersion)] = (nameof(CaseMasterDataVersion.SupersededAtUtc), []),
        [typeof(CaseApplicability)] = (nameof(CaseApplicability.SupersededAtUtc), []),
        [typeof(CaseRecipientStatement)] = (nameof(CaseRecipientStatement.SupersededAtUtc), []),
        [typeof(CaseEvidenceLink)] = (nameof(CaseEvidenceLink.WithdrawnAtUtc), [nameof(CaseEvidenceLink.WithdrawnByUserId)]),
        [typeof(CaseArchival)] = (nameof(CaseArchival.UnarchivedAtUtc), [nameof(CaseArchival.UnarchivedByUserId), nameof(CaseArchival.UnarchiveReason)]),
        [typeof(ExternalDdsRecord)] = (nameof(ExternalDdsRecord.SupersededAtUtc), []),
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
    // Recipient rows of a statement version are fully immutable and never deleted (a new fact = a new statement version).
    // The operator profile is mutable catalogue-like data, but its deletion is rejected. Application-level only, like the guards above.
    private static void EnforceCompletenessFacts(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.Entity is CaseRecipient && entry.State is EntityState.Modified or EntityState.Deleted)
            throw new InvalidOperationException("Case recipients are immutable; save a new recipient statement version.");
        if (entry.Entity is OperatorProfile && entry.State == EntityState.Deleted)
            throw new InvalidOperationException("The operator profile is never deleted.");
    }
    // Review history is fully immutable and never deleted: a new decision is a new CaseRevision and ReviewRecord. Application-level only, like the guards above:
    // other save overloads, bulk writes and raw SQL bypass it (the unique indexes and Restrict foreign keys are the database backstop).
    private static void EnforceReviewHistoryImmutable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Review history is immutable; record a new review.");
    }
    // Closures and reopenings are fully immutable and never deleted: closing again is a new CaseClosure, reopening is a new CaseReopening. Archive periods are
    // governed by the retirement map above (one-way unarchive only). Application-level only, like the guards above: other save overloads, bulk writes and raw SQL
    // bypass it (the unique indexes and Restrict foreign keys are the database backstop).
    private static void EnforceLifecycleHistoryImmutable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Case lifecycle history is immutable; record a new closure or reopening.");
    }
    // Concerns, corrective actions, notifications and resolutions are fully immutable and never deleted: a new fact is a new row (or a new concern). Application-level only, like
    // the guards above: other save overloads, bulk writes and raw SQL bypass it (the unique indexes, CHECKs and Restrict foreign keys are the database backstop).
    private static void EnforceConcernHistoryImmutable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Concern history is immutable; record a new concern fact.");
    }
    // Risk assessments, their findings and mitigation measures are fully immutable and never deleted: a later decision is a new assessment, a correction is a new measure. Application-level only,
    // like the guards above: other save overloads, bulk writes and raw SQL bypass it (the unique indexes, CHECKs and Restrict foreign keys are the database backstop).
    private static void EnforceRiskHistoryImmutable(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry)
    {
        if (entry.State is EntityState.Modified or EntityState.Deleted) throw new InvalidOperationException("Risk history is immutable; record a new risk assessment or mitigation measure.");
    }
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.Entity is AuditEvent && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit events are append-only.");
            if (entry.Entity is CaseDocument or GeoSubmission or SupplierSubmission or PlotRevision or CaseLot or CaseMasterDataVersion or CaseApplicability or CaseRecipientStatement or CaseEvidenceLink or CaseArchival or ExternalDdsRecord) EnforceRetainedEvidence(entry);
            if (entry.Entity is CaseClosure or CaseReopening) EnforceLifecycleHistoryImmutable(entry);
            if (entry.Entity is CaseConcern or ConcernAction or ConcernNotification or ConcernResolution) EnforceConcernHistoryImmutable(entry);
            if (entry.Entity is RiskAssessment or RiskAssessmentFinding or RiskMitigationMeasure) EnforceRiskHistoryImmutable(entry);
            if (entry.Entity is CaseRecipient or OperatorProfile) EnforceCompletenessFacts(entry);
            if (entry.Entity is CaseRevision or CaseRevisionDocument or CaseRevisionEvidenceLink or ReviewRecord) EnforceReviewHistoryImmutable(entry);
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
