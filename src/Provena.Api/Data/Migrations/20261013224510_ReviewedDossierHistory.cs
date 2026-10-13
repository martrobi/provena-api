using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReviewedDossierHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Submissions_OrganizationId_EudrCaseId_Id",
                table: "Submissions",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Geolocations_OrganizationId_EudrCaseId_Id",
                table: "Geolocations",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseRecipientStatements_OrganizationId_EudrCaseId_Id",
                table: "CaseRecipientStatements",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseMasterDataVersions_OrganizationId_EudrCaseId_Id",
                table: "CaseMasterDataVersions",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseLots_OrganizationId_CaseDossierId_Id",
                table: "CaseLots",
                columns: new[] { "OrganizationId", "CaseDossierId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseEvidenceLinks_OrganizationId_EudrCaseId_Id",
                table: "CaseEvidenceLinks",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseDossiers_OrganizationId_EudrCaseId_Id",
                table: "CaseDossiers",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_CaseApplicabilities_OrganizationId_EudrCaseId_Id",
                table: "CaseApplicabilities",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.CreateTable(
                name: "CaseRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CaseDossierId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseLotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseApplicabilityId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseRecipientStatementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseMasterDataVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    GeoSubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierSubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PolicyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SnapshotSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    FingerprintVersion = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SectionHashes = table.Column<string>(type: "jsonb", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseRevisions", x => x.Id);
                    table.UniqueConstraint("AK_CaseRevisions_OrganizationId_EudrCaseId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.Id });
                    table.UniqueConstraint("AK_CaseRevisions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_CaseRevisions_DossierLotPair", "(\"CaseDossierId\" IS NULL AND \"CaseLotId\" IS NULL) OR (\"CaseDossierId\" IS NOT NULL AND \"CaseLotId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_CaseRevisions_CaseApplicabilities_OrganizationId_EudrCaseId~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseApplicabilityId },
                        principalTable: "CaseApplicabilities",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_CaseDossiers_OrganizationId_EudrCaseId_CaseDo~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseDossierId },
                        principalTable: "CaseDossiers",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_CaseLots_OrganizationId_CaseDossierId_CaseLot~",
                        columns: x => new { x.OrganizationId, x.CaseDossierId, x.CaseLotId },
                        principalTable: "CaseLots",
                        principalColumns: new[] { "OrganizationId", "CaseDossierId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_CaseMasterDataVersions_OrganizationId_EudrCas~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseMasterDataVersionId },
                        principalTable: "CaseMasterDataVersions",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_CaseRecipientStatements_OrganizationId_EudrCa~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseRecipientStatementId },
                        principalTable: "CaseRecipientStatements",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_Geolocations_OrganizationId_EudrCaseId_GeoSub~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.GeoSubmissionId },
                        principalTable: "Geolocations",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisions_Submissions_OrganizationId_EudrCaseId_Supplie~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.SupplierSubmissionId },
                        principalTable: "Submissions",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseRevisionDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseRevisionDocuments", x => x.Id);
                    table.UniqueConstraint("AK_CaseRevisionDocuments_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseRevisionDocuments_CaseRevisions_OrganizationId_EudrCase~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId },
                        principalTable: "CaseRevisions",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionDocuments_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionDocuments_Documents_OrganizationId_EudrCaseId_C~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionDocuments_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseRevisionEvidenceLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseEvidenceLinkId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseRevisionEvidenceLinks", x => x.Id);
                    table.UniqueConstraint("AK_CaseRevisionEvidenceLinks_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseRevisionEvidenceLinks_CaseEvidenceLinks_OrganizationId_~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseEvidenceLinkId },
                        principalTable: "CaseEvidenceLinks",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionEvidenceLinks_CaseRevisions_OrganizationId_Eudr~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId },
                        principalTable: "CaseRevisions",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionEvidenceLinks_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseRevisionEvidenceLinks_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Decision = table.Column<int>(type: "integer", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ReviewerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewRecords", x => x.Id);
                    table.UniqueConstraint("AK_ReviewRecords_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_ReviewRecords_CaseRevisions_OrganizationId_EudrCaseId_CaseR~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseRevisionId },
                        principalTable: "CaseRevisions",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewRecords_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewRecords_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionDocuments_CaseRevisionId_CaseDocumentId",
                table: "CaseRevisionDocuments",
                columns: new[] { "CaseRevisionId", "CaseDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionDocuments_OrganizationId_EudrCaseId_CaseDocumen~",
                table: "CaseRevisionDocuments",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionDocuments_OrganizationId_EudrCaseId_CaseRevisio~",
                table: "CaseRevisionDocuments",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionEvidenceLinks_CaseRevisionId_CaseEvidenceLinkId",
                table: "CaseRevisionEvidenceLinks",
                columns: new[] { "CaseRevisionId", "CaseEvidenceLinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionEvidenceLinks_OrganizationId_EudrCaseId_CaseEvi~",
                table: "CaseRevisionEvidenceLinks",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseEvidenceLinkId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisionEvidenceLinks_OrganizationId_EudrCaseId_CaseRev~",
                table: "CaseRevisionEvidenceLinks",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_EudrCaseId_Number",
                table: "CaseRevisions",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_Fingerprint",
                table: "CaseRevisions",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_CaseDossierId_CaseLotId",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "CaseDossierId", "CaseLotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_CaseApplicabilityId",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseApplicabilityId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_CaseDossierId",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseDossierId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_CaseMasterDataVersi~",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseMasterDataVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_CaseRecipientStatem~",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseRecipientStatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_GeoSubmissionId",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "GeoSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseRevisions_OrganizationId_EudrCaseId_SupplierSubmissionId",
                table: "CaseRevisions",
                columns: new[] { "OrganizationId", "EudrCaseId", "SupplierSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewRecords_CaseRevisionId",
                table: "ReviewRecords",
                column: "CaseRevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewRecords_EudrCaseId_ReviewedAtUtc",
                table: "ReviewRecords",
                columns: new[] { "EudrCaseId", "ReviewedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewRecords_OrganizationId_EudrCaseId_CaseRevisionId",
                table: "ReviewRecords",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseRevisionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback guard: this must stay the first operation. Dropping the tables would destroy immutable review history.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""CaseRevisions"")
       OR EXISTS (SELECT 1 FROM ""CaseRevisionDocuments"")
       OR EXISTS (SELECT 1 FROM ""CaseRevisionEvidenceLinks"")
       OR EXISTS (SELECT 1 FROM ""ReviewRecords"")
    THEN
        RAISE EXCEPTION 'Review history records exist; rollback would destroy history';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "CaseRevisionDocuments");

            migrationBuilder.DropTable(
                name: "CaseRevisionEvidenceLinks");

            migrationBuilder.DropTable(
                name: "ReviewRecords");

            migrationBuilder.DropTable(
                name: "CaseRevisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Submissions_OrganizationId_EudrCaseId_Id",
                table: "Submissions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Geolocations_OrganizationId_EudrCaseId_Id",
                table: "Geolocations");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseRecipientStatements_OrganizationId_EudrCaseId_Id",
                table: "CaseRecipientStatements");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseMasterDataVersions_OrganizationId_EudrCaseId_Id",
                table: "CaseMasterDataVersions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseLots_OrganizationId_CaseDossierId_Id",
                table: "CaseLots");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseEvidenceLinks_OrganizationId_EudrCaseId_Id",
                table: "CaseEvidenceLinks");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseDossiers_OrganizationId_EudrCaseId_Id",
                table: "CaseDossiers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_CaseApplicabilities_OrganizationId_EudrCaseId_Id",
                table: "CaseApplicabilities");
        }
    }
}
