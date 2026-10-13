using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CompletenessFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PostalAddress",
                table: "Suppliers",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Documents_OrganizationId_EudrCaseId_Id",
                table: "Documents",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.CreateTable(
                name: "CaseApplicabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    AssessedProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessedCnCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    AssessedCommodity = table.Column<int>(type: "integer", nullable: false),
                    ProductDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SingleRelevantCommodity = table.Column<bool>(type: "boolean", nullable: false),
                    ActivityType = table.Column<int>(type: "integer", nullable: false),
                    OperatorRole = table.Column<int>(type: "integer", nullable: false),
                    OperatorRoleDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ActivityDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ActivityDateKind = table.Column<int>(type: "integer", nullable: false),
                    ScopeCitation = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Qualifications = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LegalBasisVersion = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Reasoning = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    RegulatoryScope = table.Column<int>(type: "integer", nullable: false),
                    FlowSupport = table.Column<int>(type: "integer", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseApplicabilities", x => x.Id);
                    table.UniqueConstraint("AK_CaseApplicabilities_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseApplicabilities_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseApplicabilities_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseApplicabilities_Products_OrganizationId_AssessedProduct~",
                        columns: x => new { x.OrganizationId, x.AssessedProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseEvidenceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    WithdrawnAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WithdrawnByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseEvidenceLinks", x => x.Id);
                    table.UniqueConstraint("AK_CaseEvidenceLinks_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseEvidenceLinks_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvidenceLinks_Documents_OrganizationId_EudrCaseId_CaseD~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseEvidenceLinks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseMasterDataVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    OperatorLegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OperatorPostalAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OperatorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    OperatorEstablishmentCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    OperatorEori = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SupplierPostalAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SupplierEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SupplierCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductCnCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ProductCommodity = table.Column<int>(type: "integer", nullable: false),
                    ProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProductCatalogueDescription = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseMasterDataVersions", x => x.Id);
                    table.UniqueConstraint("AK_CaseMasterDataVersions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseMasterDataVersions_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseMasterDataVersions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseMasterDataVersions_Products_OrganizationId_ProductId",
                        columns: x => new { x.OrganizationId, x.ProductId },
                        principalTable: "Products",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseMasterDataVersions_Suppliers_OrganizationId_SupplierId",
                        columns: x => new { x.OrganizationId, x.SupplierId },
                        principalTable: "Suppliers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseRecipientStatements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Resolution = table.Column<int>(type: "integer", nullable: false),
                    NotApplicableBasis = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseRecipientStatements", x => x.Id);
                    table.UniqueConstraint("AK_CaseRecipientStatements_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseRecipientStatements_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRecipientStatements_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperatorProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostalAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EstablishmentCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    EoriNumber = table.Column<string>(type: "character varying(17)", maxLength: 17, nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorProfiles", x => x.Id);
                    table.UniqueConstraint("AK_OperatorProfiles_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_OperatorProfiles_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseRecipients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRecipientStatementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PostalAddress = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseRecipients", x => x.Id);
                    table.UniqueConstraint("AK_CaseRecipients_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseRecipients_CaseRecipientStatements_OrganizationId_CaseR~",
                        columns: x => new { x.OrganizationId, x.CaseRecipientStatementId },
                        principalTable: "CaseRecipientStatements",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRecipients_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseApplicabilities_EudrCaseId",
                table: "CaseApplicabilities",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseApplicabilities_EudrCaseId_Number",
                table: "CaseApplicabilities",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseApplicabilities_OrganizationId_AssessedProductId",
                table: "CaseApplicabilities",
                columns: new[] { "OrganizationId", "AssessedProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseApplicabilities_OrganizationId_EudrCaseId",
                table: "CaseApplicabilities",
                columns: new[] { "OrganizationId", "EudrCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvidenceLinks_EudrCaseId_CaseDocumentId_Purpose",
                table: "CaseEvidenceLinks",
                columns: new[] { "EudrCaseId", "CaseDocumentId", "Purpose" },
                unique: true,
                filter: "\"WithdrawnAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseEvidenceLinks_OrganizationId_EudrCaseId_CaseDocumentId",
                table: "CaseEvidenceLinks",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseMasterDataVersions_EudrCaseId",
                table: "CaseMasterDataVersions",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseMasterDataVersions_EudrCaseId_Number",
                table: "CaseMasterDataVersions",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseMasterDataVersions_OrganizationId_EudrCaseId",
                table: "CaseMasterDataVersions",
                columns: new[] { "OrganizationId", "EudrCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseMasterDataVersions_OrganizationId_ProductId",
                table: "CaseMasterDataVersions",
                columns: new[] { "OrganizationId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseMasterDataVersions_OrganizationId_SupplierId",
                table: "CaseMasterDataVersions",
                columns: new[] { "OrganizationId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRecipients_OrganizationId_CaseRecipientStatementId",
                table: "CaseRecipients",
                columns: new[] { "OrganizationId", "CaseRecipientStatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRecipientStatements_EudrCaseId",
                table: "CaseRecipientStatements",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseRecipientStatements_EudrCaseId_Number",
                table: "CaseRecipientStatements",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseRecipientStatements_OrganizationId_EudrCaseId",
                table: "CaseRecipientStatements",
                columns: new[] { "OrganizationId", "EudrCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_OperatorProfiles_OrganizationId",
                table: "OperatorProfiles",
                column: "OrganizationId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback guard: this must stay the first operation. Dropping the tables or the column would destroy completeness-fact history.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""OperatorProfiles"")
       OR EXISTS (SELECT 1 FROM ""CaseMasterDataVersions"")
       OR EXISTS (SELECT 1 FROM ""CaseApplicabilities"")
       OR EXISTS (SELECT 1 FROM ""CaseRecipientStatements"")
       OR EXISTS (SELECT 1 FROM ""CaseRecipients"")
       OR EXISTS (SELECT 1 FROM ""CaseEvidenceLinks"")
       OR EXISTS (SELECT 1 FROM ""Suppliers"" WHERE ""PostalAddress"" IS NOT NULL)
    THEN
        RAISE EXCEPTION 'Completeness fact records exist; rollback would destroy history';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "CaseApplicabilities");

            migrationBuilder.DropTable(
                name: "CaseEvidenceLinks");

            migrationBuilder.DropTable(
                name: "CaseMasterDataVersions");

            migrationBuilder.DropTable(
                name: "CaseRecipients");

            migrationBuilder.DropTable(
                name: "OperatorProfiles");

            migrationBuilder.DropTable(
                name: "CaseRecipientStatements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Documents_OrganizationId_EudrCaseId_Id",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "PostalAddress",
                table: "Suppliers");
        }
    }
}
