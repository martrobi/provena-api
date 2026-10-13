// Current model snapshot. EF rewrites this file for each new migration; InitialCreate's target model is frozen in InitialCreate.TargetModel.cs.
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
namespace Provena.Api.Data.Migrations;
[DbContext(typeof(AppDbContext))]
public sealed class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.12").HasAnnotation("Relational:MaxIdentifierLength", 63);
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);
        modelBuilder.Entity("Provena.Api.Domain.ApplicationUser", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("UserName").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("NormalizedUserName").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("Email").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("NormalizedEmail").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<bool>("EmailConfirmed").HasColumnType("boolean");
            b.Property<string>("PasswordHash").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("SecurityStamp").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ConcurrencyStamp").IsConcurrencyToken().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("PhoneNumber").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("PhoneNumberConfirmed").HasColumnType("boolean");
            b.Property<bool>("TwoFactorEnabled").HasColumnType("boolean");
            b.Property<DateTimeOffset?>("LockoutEnd").HasColumnType("timestamp with time zone");
            b.Property<bool>("LockoutEnabled").HasColumnType("boolean");
            b.Property<int>("AccessFailedCount").HasColumnType("integer");
            b.Property<string>("FirstName").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<string>("LastName").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasIndex("NormalizedEmail").IsUnique().HasDatabaseName("EmailIndex");
            b.HasIndex("NormalizedUserName").IsUnique().HasDatabaseName("UserNameIndex");
            b.ToTable("AspNetUsers", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRole<System.Guid>", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<string>("Name").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("NormalizedName").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("ConcurrencyStamp").IsConcurrencyToken().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasIndex("NormalizedName").IsUnique().HasDatabaseName("RoleNameIndex");
            b.ToTable("AspNetRoles", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRoleClaim<System.Guid>", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("integer");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));
            b.Property<Guid>("RoleId").HasColumnType("uuid");
            b.Property<string>("ClaimType").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ClaimValue").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasIndex("RoleId").HasDatabaseName("IX_AspNetRoleClaims_RoleId");
            b.ToTable("AspNetRoleClaims", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserClaim<System.Guid>", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("integer");
            NpgsqlPropertyBuilderExtensions.UseIdentityByDefaultColumn(b.Property<int>("Id"));
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<string>("ClaimType").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ClaimValue").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasIndex("UserId").HasDatabaseName("IX_AspNetUserClaims_UserId");
            b.ToTable("AspNetUserClaims", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserLogin<System.Guid>", b =>
        {
            b.Property<string>("LoginProvider").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ProviderKey").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ProviderDisplayName").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.HasKey("LoginProvider", "ProviderKey");
            b.HasIndex("UserId").HasDatabaseName("IX_AspNetUserLogins_UserId");
            b.ToTable("AspNetUserLogins", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserRole<System.Guid>", b =>
        {
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<Guid>("RoleId").HasColumnType("uuid");
            b.HasKey("UserId", "RoleId");
            b.HasIndex("RoleId").HasDatabaseName("IX_AspNetUserRoles_RoleId");
            b.ToTable("AspNetUserRoles", (string?)null);
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserToken<System.Guid>", b =>
        {
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<string>("LoginProvider").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Name").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Value").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("UserId", "LoginProvider", "Name");
            b.ToTable("AspNetUserTokens", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.Organization", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("Oib").HasMaxLength(11).HasColumnType("character varying(11)");
            b.HasKey("Id");
            b.ToTable("Organizations", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.OrganizationUser", b =>
        {
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("UserId").HasColumnType("uuid");
            b.Property<int>("Role").HasColumnType("integer");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("OrganizationId", "UserId");
            b.HasIndex("UserId").IsUnique().HasDatabaseName("IX_OrganizationUsers_UserId");
            b.ToTable("OrganizationUsers", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.Supplier", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ExternalReference").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Email").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("PostalAddress").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("ContactPerson").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("CountryCode").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "ExternalReference").IsUnique().HasDatabaseName("IX_Suppliers_OrganizationId_ExternalReference").HasFilter("\"ExternalReference\" IS NOT NULL");
            b.ToTable("Suppliers", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.Product", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ExternalReference").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("CnCode").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("CommodityType").HasColumnType("integer");
            b.Property<string>("Description").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("IsActive").HasColumnType("boolean");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "ExternalReference").IsUnique().HasDatabaseName("IX_Products_OrganizationId_ExternalReference").HasFilter("\"ExternalReference\" IS NOT NULL");
            b.ToTable("Products", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.Shipment", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("ReferenceNumber").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid>("SupplierId").HasColumnType("uuid");
            b.Property<Guid>("ProductId").HasColumnType("uuid");
            b.Property<decimal>("Quantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<int>("Unit").HasColumnType("integer");
            b.Property<string>("OriginCountryCode").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateOnly?>("ExpectedArrivalDate").HasColumnType("date");
            b.Property<DateOnly?>("ReceivedDate").HasColumnType("date");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "ReferenceNumber").IsUnique().HasDatabaseName("IX_Shipments_OrganizationId_ReferenceNumber");
            b.HasIndex("OrganizationId", "SupplierId").HasDatabaseName("IX_Shipments_OrganizationId_SupplierId");
            b.HasIndex("OrganizationId", "ProductId").HasDatabaseName("IX_Shipments_OrganizationId_ProductId");
            b.ToTable("Shipments", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.EudrCase", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("ShipmentId").HasColumnType("uuid");
            b.Property<int>("Status").HasColumnType("integer");
            b.Property<DateTimeOffset?>("SupplierSubmissionCompletedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("InternalReviewCompletedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("CompletedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<string>("InternalNotes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ExternalDdsReference").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("ShipmentId").IsUnique().HasDatabaseName("IX_Cases_ShipmentId");
            b.HasIndex("OrganizationId", "Status").HasDatabaseName("IX_Cases_OrganizationId_Status");
            b.HasIndex("OrganizationId", "ShipmentId").HasDatabaseName("IX_Cases_OrganizationId_ShipmentId");
            b.ToTable("Cases", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.EudrRequirement", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<int>("Type").HasColumnType("integer");
            b.Property<string>("Title").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Description").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("IsRequired").HasColumnType("boolean");
            b.Property<int>("Status").HasColumnType("integer");
            b.Property<string>("ReviewerNote").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("ReviewedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("ReviewedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_Requirements_OrganizationId_EudrCaseId");
            b.HasIndex("EudrCaseId", "Type").IsUnique().HasDatabaseName("IX_Requirements_EudrCaseId_Type");
            b.ToTable("Requirements", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.SupplierRequest", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<Guid>("SupplierId").HasColumnType("uuid");
            b.Property<string>("TokenHash").IsRequired().HasMaxLength(64).HasColumnType("character varying(64)");
            b.Property<DateTimeOffset>("ExpiresAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("SentAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("OpenedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("CompletedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset?>("RevokedAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_SupplierRequests_OrganizationId_EudrCaseId");
            b.HasIndex("TokenHash").IsUnique().HasDatabaseName("IX_SupplierRequests_TokenHash");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_SupplierRequests_EudrCaseId").HasFilter("\"RevokedAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "SupplierId").HasDatabaseName("IX_SupplierRequests_OrganizationId_SupplierId");
            b.ToTable("SupplierRequests", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.SupplierSubmission", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<string>("CountryOfProductionCode").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateOnly?>("ProductionPeriodFrom").HasColumnType("date");
            b.Property<DateOnly?>("ProductionPeriodTo").HasColumnType("date");
            b.Property<decimal?>("ConfirmedQuantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateTimeOffset?>("SubmittedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("SupplierRequestId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_Submissions_OrganizationId_EudrCaseId");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_Submissions_EudrCaseId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.ToTable("Submissions", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseDocument", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<Guid?>("SupplierRequestId").HasColumnType("uuid");
            b.Property<string>("OriginalFileName").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("StoredFileName").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ContentType").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<long>("SizeBytes").HasColumnType("bigint");
            b.Property<int>("Category").HasColumnType("integer");
            b.Property<string>("StorageKey").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("UploadedByType").HasColumnType("integer");
            b.Property<DateTimeOffset>("UploadedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("UploadedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("WithdrawnAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("WithdrawnByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasAlternateKey("OrganizationId", "EudrCaseId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_Documents_OrganizationId_EudrCaseId");
            b.ToTable("Documents", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.GeoSubmission", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<Guid?>("SupplierRequestId").HasColumnType("uuid");
            b.Property<string>("OriginalFileName").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("StorageKey").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("RawGeoJson").IsRequired().HasColumnType("jsonb");
            b.Property<int>("FeatureCount").HasColumnType("integer");
            b.Property<string>("GeometryTypes").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("ValidationStatus").HasColumnType("integer");
            b.Property<string>("ValidationMessage").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<DateTimeOffset>("UploadedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("UploadedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_Geolocations_OrganizationId_EudrCaseId");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_Geolocations_EudrCaseId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.ToTable("Geolocations", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.Producer", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ExternalReference").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("CountryCode").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("IsActive").HasColumnType("boolean");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "ExternalReference").IsUnique().HasDatabaseName("IX_Producers_OrganizationId_ExternalReference").HasFilter("\"ExternalReference\" IS NOT NULL");
            b.ToTable("Producers", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.ProductionPlot", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("ProducerId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("ExternalReference").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Notes").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<bool>("IsActive").HasColumnType("boolean");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasAlternateKey("OrganizationId", "ProducerId", "Id");
            b.HasIndex("OrganizationId", "ProducerId").HasDatabaseName("IX_ProductionPlots_OrganizationId_ProducerId");
            b.HasIndex("OrganizationId", "ExternalReference").IsUnique().HasDatabaseName("IX_ProductionPlots_OrganizationId_ExternalReference").HasFilter("\"ExternalReference\" IS NOT NULL");
            b.ToTable("ProductionPlots", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.PlotRevision", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("ProductionPlotId").HasColumnType("uuid");
            b.Property<int>("Number").HasColumnType("integer");
            b.Property<string>("CountryCode").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("GeometryType").HasColumnType("integer");
            b.Property<string>("GeometryJson").IsRequired().HasColumnType("jsonb");
            b.Property<decimal?>("AreaHectares").HasPrecision(12, 4).HasColumnType("numeric(12,4)");
            b.Property<string>("Note").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasAlternateKey("OrganizationId", "ProductionPlotId", "Id");
            b.HasIndex("OrganizationId", "ProductionPlotId").HasDatabaseName("IX_PlotRevisions_OrganizationId_ProductionPlotId");
            b.HasIndex("ProductionPlotId", "Number").IsUnique().HasDatabaseName("IX_PlotRevisions_ProductionPlotId_Number");
            b.HasIndex("ProductionPlotId").IsUnique().HasDatabaseName("IX_PlotRevisions_ProductionPlotId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.ToTable("PlotRevisions", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseDossier", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_CaseDossiers_EudrCaseId");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_CaseDossiers_OrganizationId_EudrCaseId");
            b.ToTable("CaseDossiers", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseLot", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("CaseDossierId").HasColumnType("uuid");
            b.Property<int>("Number").HasColumnType("integer");
            b.Property<string>("LotReference").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)");
            b.Property<decimal>("Quantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<int>("Unit").HasColumnType("integer");
            b.Property<string>("Note").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("CaseDossierId", "Number").IsUnique().HasDatabaseName("IX_CaseLots_CaseDossierId_Number");
            b.HasIndex("CaseDossierId").IsUnique().HasDatabaseName("IX_CaseLots_CaseDossierId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "LotReference").IsUnique().HasDatabaseName("IX_CaseLots_OrganizationId_LotReference").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "CaseDossierId").HasDatabaseName("IX_CaseLots_OrganizationId_CaseDossierId");
            b.ToTable("CaseLots", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.LotContribution", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("CaseLotId").HasColumnType("uuid");
            b.Property<Guid>("ProducerId").HasColumnType("uuid");
            b.Property<decimal>("Quantity").HasPrecision(20, 6).HasColumnType("numeric(20,6)");
            b.Property<int>("Unit").HasColumnType("integer");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasAlternateKey("OrganizationId", "Id", "ProducerId");
            b.HasIndex("CaseLotId", "ProducerId").IsUnique().HasDatabaseName("IX_LotContributions_CaseLotId_ProducerId");
            b.HasIndex("OrganizationId", "CaseLotId").HasDatabaseName("IX_LotContributions_OrganizationId_CaseLotId");
            b.HasIndex("OrganizationId", "ProducerId").HasDatabaseName("IX_LotContributions_OrganizationId_ProducerId");
            b.ToTable("LotContributions", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.ContributionPlot", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("LotContributionId").HasColumnType("uuid");
            b.Property<Guid>("ProducerId").HasColumnType("uuid");
            b.Property<Guid>("ProductionPlotId").HasColumnType("uuid");
            b.Property<Guid>("PlotRevisionId").HasColumnType("uuid");
            b.Property<DateOnly?>("ProductionStartDate").HasColumnType("date");
            b.Property<DateOnly?>("ProductionEndDate").HasColumnType("date");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("LotContributionId", "ProductionPlotId").IsUnique().HasDatabaseName("IX_ContributionPlots_LotContributionId_ProductionPlotId");
            b.HasIndex("OrganizationId", "LotContributionId", "ProducerId").HasDatabaseName("IX_ContributionPlots_OrganizationId_LotContributionId_Producer~");
            b.HasIndex("OrganizationId", "ProducerId", "ProductionPlotId").HasDatabaseName("IX_ContributionPlots_OrganizationId_ProducerId_ProductionPlotId");
            b.HasIndex("OrganizationId", "ProductionPlotId", "PlotRevisionId").HasDatabaseName("IX_ContributionPlots_OrganizationId_ProductionPlotId_PlotRevis~");
            b.ToTable("ContributionPlots", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.OperatorProfile", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<string>("LegalName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("PostalAddress").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Email").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("EstablishmentCountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<string>("EoriNumber").HasMaxLength(17).HasColumnType("character varying(17)");
            b.Property<Guid?>("UpdatedByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId").IsUnique().HasDatabaseName("IX_OperatorProfiles_OrganizationId");
            b.ToTable("OperatorProfiles", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseMasterDataVersion", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<int>("Number").HasColumnType("integer");
            b.Property<string>("OperatorLegalName").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("OperatorPostalAddress").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("OperatorEmail").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("OperatorEstablishmentCountryCode").HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<string>("OperatorEori").HasMaxLength(17).HasColumnType("character varying(17)");
            b.Property<Guid>("SupplierId").HasColumnType("uuid");
            b.Property<string>("SupplierName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("SupplierPostalAddress").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("SupplierEmail").HasMaxLength(256).HasColumnType("character varying(256)");
            b.Property<string>("SupplierCountryCode").HasMaxLength(2).HasColumnType("character varying(2)");
            b.Property<Guid>("ProductId").HasColumnType("uuid");
            b.Property<string>("ProductCnCode").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<int>("ProductCommodity").HasColumnType("integer");
            b.Property<string>("ProductName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("ProductCatalogueDescription").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("EudrCaseId", "Number").IsUnique().HasDatabaseName("IX_CaseMasterDataVersions_EudrCaseId_Number");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_CaseMasterDataVersions_EudrCaseId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_CaseMasterDataVersions_OrganizationId_EudrCaseId");
            b.HasIndex("OrganizationId", "ProductId").HasDatabaseName("IX_CaseMasterDataVersions_OrganizationId_ProductId");
            b.HasIndex("OrganizationId", "SupplierId").HasDatabaseName("IX_CaseMasterDataVersions_OrganizationId_SupplierId");
            b.ToTable("CaseMasterDataVersions", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseApplicability", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<int>("Number").HasColumnType("integer");
            b.Property<Guid>("AssessedProductId").HasColumnType("uuid");
            b.Property<string>("AssessedCnCode").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)");
            b.Property<int>("AssessedCommodity").HasColumnType("integer");
            b.Property<string>("ProductDescription").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<bool>("SingleRelevantCommodity").HasColumnType("boolean");
            b.Property<int>("ActivityType").HasColumnType("integer");
            b.Property<int>("OperatorRole").HasColumnType("integer");
            b.Property<string>("OperatorRoleDescription").HasMaxLength(500).HasColumnType("character varying(500)");
            b.Property<DateOnly?>("ActivityDate").HasColumnType("date");
            b.Property<int>("ActivityDateKind").HasColumnType("integer");
            b.Property<string>("ScopeCitation").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Qualifications").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<string>("LegalBasisVersion").HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<DateOnly?>("EffectiveDate").HasColumnType("date");
            b.Property<string>("Reasoning").HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<int>("RegulatoryScope").HasColumnType("integer");
            b.Property<int>("FlowSupport").HasColumnType("integer");
            b.Property<Guid?>("DecidedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("DecidedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("EudrCaseId", "Number").IsUnique().HasDatabaseName("IX_CaseApplicabilities_EudrCaseId_Number");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_CaseApplicabilities_EudrCaseId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "AssessedProductId").HasDatabaseName("IX_CaseApplicabilities_OrganizationId_AssessedProductId");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_CaseApplicabilities_OrganizationId_EudrCaseId");
            b.ToTable("CaseApplicabilities", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseRecipientStatement", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<int>("Number").HasColumnType("integer");
            b.Property<int>("Resolution").HasColumnType("integer");
            b.Property<string>("NotApplicableBasis").HasMaxLength(2000).HasColumnType("character varying(2000)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("SupersededAtUtc").HasColumnType("timestamp with time zone");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("EudrCaseId", "Number").IsUnique().HasDatabaseName("IX_CaseRecipientStatements_EudrCaseId_Number");
            b.HasIndex("EudrCaseId").IsUnique().HasDatabaseName("IX_CaseRecipientStatements_EudrCaseId").HasFilter("\"SupersededAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_CaseRecipientStatements_OrganizationId_EudrCaseId");
            b.ToTable("CaseRecipientStatements", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseRecipient", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("CaseRecipientStatementId").HasColumnType("uuid");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)");
            b.Property<string>("PostalAddress").IsRequired().HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<string>("Email").IsRequired().HasMaxLength(256).HasColumnType("character varying(256)");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "CaseRecipientStatementId").HasDatabaseName("IX_CaseRecipients_OrganizationId_CaseRecipientStatementId");
            b.ToTable("CaseRecipients", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseEvidenceLink", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid>("EudrCaseId").HasColumnType("uuid");
            b.Property<Guid>("CaseDocumentId").HasColumnType("uuid");
            b.Property<int>("Purpose").HasColumnType("integer");
            b.Property<string>("Note").HasMaxLength(1000).HasColumnType("character varying(1000)");
            b.Property<Guid?>("CreatedByUserId").HasColumnType("uuid");
            b.Property<DateTimeOffset?>("WithdrawnAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid?>("WithdrawnByUserId").HasColumnType("uuid");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("EudrCaseId", "CaseDocumentId", "Purpose").IsUnique().HasDatabaseName("IX_CaseEvidenceLinks_EudrCaseId_CaseDocumentId_Purpose").HasFilter("\"WithdrawnAtUtc\" IS NULL");
            b.HasIndex("OrganizationId", "EudrCaseId", "CaseDocumentId").HasDatabaseName("IX_CaseEvidenceLinks_OrganizationId_EudrCaseId_CaseDocumentId");
            b.ToTable("CaseEvidenceLinks", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.AuditEvent", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uuid");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<DateTimeOffset>("UpdatedAtUtc").HasColumnType("timestamp with time zone");
            b.Property<Guid>("OrganizationId").HasColumnType("uuid");
            b.Property<Guid?>("EudrCaseId").HasColumnType("uuid");
            b.Property<int>("ActorType").HasColumnType("integer");
            b.Property<Guid?>("ActorUserId").HasColumnType("uuid");
            b.Property<Guid?>("SupplierRequestId").HasColumnType("uuid");
            b.Property<string>("EventType").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("Description").IsRequired().HasMaxLength(4000).HasColumnType("character varying(4000)");
            b.Property<string>("MetadataJson").HasColumnType("jsonb");
            b.HasKey("Id");
            b.HasAlternateKey("OrganizationId", "Id");
            b.HasIndex("OrganizationId", "EudrCaseId").HasDatabaseName("IX_AuditEvents_OrganizationId_EudrCaseId");
            b.HasIndex("EudrCaseId", "CreatedAtUtc").HasDatabaseName("IX_AuditEvents_EudrCaseId_CreatedAtUtc");
            b.ToTable("AuditEvents", (string?)null);
        });
        modelBuilder.Entity("Provena.Api.Domain.ApplicationUser", b =>
        {
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRole<System.Guid>", b =>
        {
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityRoleClaim<System.Guid>", b =>
        {
            b.HasOne("Microsoft.AspNetCore.Identity.IdentityRole<System.Guid>", null).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserClaim<System.Guid>", b =>
        {
            b.HasOne("Provena.Api.Domain.ApplicationUser", null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserLogin<System.Guid>", b =>
        {
            b.HasOne("Provena.Api.Domain.ApplicationUser", null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserRole<System.Guid>", b =>
        {
            b.HasOne("Provena.Api.Domain.ApplicationUser", null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            b.HasOne("Microsoft.AspNetCore.Identity.IdentityRole<System.Guid>", null).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Microsoft.AspNetCore.Identity.IdentityUserToken<System.Guid>", b =>
        {
            b.HasOne("Provena.Api.Domain.ApplicationUser", null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.Organization", b =>
        {
        });
        modelBuilder.Entity("Provena.Api.Domain.OrganizationUser", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.ApplicationUser", null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.Supplier", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.Product", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.Shipment", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Supplier", null).WithMany().HasForeignKey("OrganizationId", "SupplierId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Product", null).WithMany().HasForeignKey("OrganizationId", "ProductId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.EudrCase", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Shipment", null).WithMany().HasForeignKey("OrganizationId", "ShipmentId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.EudrRequirement", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.SupplierRequest", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Supplier", null).WithMany().HasForeignKey("OrganizationId", "SupplierId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.SupplierSubmission", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseDocument", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.GeoSubmission", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.AuditEvent", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity("Provena.Api.Domain.Producer", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.ProductionPlot", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Producer", null).WithMany().HasForeignKey("OrganizationId", "ProducerId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.PlotRevision", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.ProductionPlot", null).WithMany().HasForeignKey("OrganizationId", "ProductionPlotId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseDossier", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseLot", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.CaseDossier", null).WithMany().HasForeignKey("OrganizationId", "CaseDossierId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.LotContribution", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.CaseLot", null).WithMany().HasForeignKey("OrganizationId", "CaseLotId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Producer", null).WithMany().HasForeignKey("OrganizationId", "ProducerId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.ContributionPlot", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.LotContribution", null).WithMany().HasForeignKey("OrganizationId", "LotContributionId", "ProducerId").HasPrincipalKey("OrganizationId", "Id", "ProducerId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.ProductionPlot", null).WithMany().HasForeignKey("OrganizationId", "ProducerId", "ProductionPlotId").HasPrincipalKey("OrganizationId", "ProducerId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.PlotRevision", null).WithMany().HasForeignKey("OrganizationId", "ProductionPlotId", "PlotRevisionId").HasPrincipalKey("OrganizationId", "ProductionPlotId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.OperatorProfile", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseMasterDataVersion", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Supplier", null).WithMany().HasForeignKey("OrganizationId", "SupplierId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Product", null).WithMany().HasForeignKey("OrganizationId", "ProductId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseApplicability", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.Product", null).WithMany().HasForeignKey("OrganizationId", "AssessedProductId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseRecipientStatement", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseRecipient", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.CaseRecipientStatement", null).WithMany().HasForeignKey("OrganizationId", "CaseRecipientStatementId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
        modelBuilder.Entity("Provena.Api.Domain.CaseEvidenceLink", b =>
        {
            b.HasOne("Provena.Api.Domain.Organization", null).WithMany().HasForeignKey("OrganizationId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.EudrCase", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId").HasPrincipalKey("OrganizationId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
            b.HasOne("Provena.Api.Domain.CaseDocument", null).WithMany().HasForeignKey("OrganizationId", "EudrCaseId", "CaseDocumentId").HasPrincipalKey("OrganizationId", "EudrCaseId", "Id").OnDelete(DeleteBehavior.Restrict).IsRequired();
        });
    }
}
