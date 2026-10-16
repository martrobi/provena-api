using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RiskAssessmentAndMitigation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId",
                table: "CaseClosures");

            migrationBuilder.AddColumn<Guid>(
                name: "RiskAssessmentId",
                table: "CaseClosures",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RiskAssessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ReviewRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriteriaCatalogueId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CriteriaCatalogueVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Conclusion = table.Column<int>(type: "integer", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ConsideredMitigationThroughNumber = table.Column<int>(type: "integer", nullable: true),
                    AssessedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssessedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskAssessments", x => x.Id);
                    table.UniqueConstraint("AK_RiskAssessments_OrganizationId_EudrCaseId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.Id });
                    table.UniqueConstraint("AK_RiskAssessments_OrganizationId_EudrCaseId_ReviewRecordId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId, x.Id });
                    table.UniqueConstraint("AK_RiskAssessments_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_RiskAssessments_MitigationNumber", "\"ConsideredMitigationThroughNumber\" IS NULL OR \"ConsideredMitigationThroughNumber\" > 0");
                    table.CheckConstraint("CK_RiskAssessments_NonblankRationale", "btrim(\"Rationale\") <> ''");
                    table.ForeignKey(
                        name: "FK_RiskAssessments_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskAssessments_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskAssessments_ReviewRecords_OrganizationId_EudrCaseId_Rev~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId },
                        principalTable: "ReviewRecords",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskAssessmentFindings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    RiskAssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CriterionCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Finding = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskAssessmentFindings", x => x.Id);
                    table.UniqueConstraint("AK_RiskAssessmentFindings_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_RiskAssessmentFindings_NonblankNote", "\"Note\" IS NULL OR btrim(\"Note\") <> ''");
                    table.CheckConstraint("CK_RiskAssessmentFindings_NoteRequired", "\"Finding\" = 0 OR \"Note\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_RiskAssessmentFindings_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskAssessmentFindings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskAssessmentFindings_RiskAssessments_OrganizationId_EudrC~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.RiskAssessmentId },
                        principalTable: "RiskAssessments",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskMitigationMeasures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    RiskAssessmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    PerformedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskMitigationMeasures", x => x.Id);
                    table.UniqueConstraint("AK_RiskMitigationMeasures_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_RiskMitigationMeasures_NonblankDescription", "btrim(\"Description\") <> ''");
                    table.ForeignKey(
                        name: "FK_RiskMitigationMeasures_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskMitigationMeasures_Documents_OrganizationId_EudrCaseId_~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskMitigationMeasures_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskMitigationMeasures_RiskAssessments_OrganizationId_EudrC~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.RiskAssessmentId },
                        principalTable: "RiskAssessments",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId_RiskA~",
                table: "CaseClosures",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReviewRecordId", "RiskAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_RiskAssessmentId",
                table: "CaseClosures",
                column: "RiskAssessmentId",
                unique: true,
                filter: "\"RiskAssessmentId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CaseClosures_RiskAssessmentOutcome",
                table: "CaseClosures",
                sql: "\"RiskAssessmentId\" IS NULL OR \"Outcome\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessmentFindings_OrganizationId_EudrCaseId_RiskAssess~",
                table: "RiskAssessmentFindings",
                columns: new[] { "OrganizationId", "EudrCaseId", "RiskAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessmentFindings_RiskAssessmentId_CriterionCode",
                table: "RiskAssessmentFindings",
                columns: new[] { "RiskAssessmentId", "CriterionCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_EudrCaseId_Number",
                table: "RiskAssessments",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskAssessments_ReviewRecordId",
                table: "RiskAssessments",
                column: "ReviewRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskMitigationMeasures_EudrCaseId_Number",
                table: "RiskMitigationMeasures",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskMitigationMeasures_OrganizationId_EudrCaseId_EvidenceDo~",
                table: "RiskMitigationMeasures",
                columns: new[] { "OrganizationId", "EudrCaseId", "EvidenceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskMitigationMeasures_OrganizationId_EudrCaseId_RiskAssess~",
                table: "RiskMitigationMeasures",
                columns: new[] { "OrganizationId", "EudrCaseId", "RiskAssessmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskMitigationMeasures_RiskAssessmentId",
                table: "RiskMitigationMeasures",
                column: "RiskAssessmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_CaseClosures_RiskAssessments_OrganizationId_EudrCaseId_Revi~",
                table: "CaseClosures",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReviewRecordId", "RiskAssessmentId" },
                principalTable: "RiskAssessments",
                principalColumns: new[] { "OrganizationId", "EudrCaseId", "ReviewRecordId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses once risk records exist (like the earlier retention migrations): the three tables are retained history, and a closure that references an assessment is retained history too.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "RiskAssessments")
                        OR EXISTS (SELECT 1 FROM "RiskAssessmentFindings")
                        OR EXISTS (SELECT 1 FROM "RiskMitigationMeasures")
                        OR EXISTS (SELECT 1 FROM "CaseClosures" WHERE "RiskAssessmentId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Risk records exist; rollback would destroy history';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_CaseClosures_RiskAssessments_OrganizationId_EudrCaseId_Revi~",
                table: "CaseClosures");

            migrationBuilder.DropTable(
                name: "RiskAssessmentFindings");

            migrationBuilder.DropTable(
                name: "RiskMitigationMeasures");

            migrationBuilder.DropTable(
                name: "RiskAssessments");

            migrationBuilder.DropIndex(
                name: "IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId_RiskA~",
                table: "CaseClosures");

            migrationBuilder.DropIndex(
                name: "IX_CaseClosures_RiskAssessmentId",
                table: "CaseClosures");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CaseClosures_RiskAssessmentOutcome",
                table: "CaseClosures");

            migrationBuilder.DropColumn(
                name: "RiskAssessmentId",
                table: "CaseClosures");

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId",
                table: "CaseClosures",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReviewRecordId" });
        }
    }
}
