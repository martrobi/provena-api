using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class PostCompletionConcerns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseConcerns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    RaisedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseConcerns", x => x.Id);
                    table.UniqueConstraint("AK_CaseConcerns_OrganizationId_EudrCaseId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.Id });
                    table.UniqueConstraint("AK_CaseConcerns_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_CaseConcerns_NonblankText", "btrim(\"Summary\") <> '' AND btrim(\"Details\") <> ''");
                    table.ForeignKey(
                        name: "FK_CaseConcerns_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseConcerns_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConcernActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcernId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    PerformedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcernActions", x => x.Id);
                    table.UniqueConstraint("AK_ConcernActions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ConcernActions_NonblankDescription", "btrim(\"Description\") <> ''");
                    table.ForeignKey(
                        name: "FK_ConcernActions_CaseConcerns_OrganizationId_EudrCaseId_Conce~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId },
                        principalTable: "CaseConcerns",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernActions_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernActions_Documents_OrganizationId_EudrCaseId_Evidence~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernActions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConcernNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcernId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRecipientId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<int>(type: "integer", nullable: false),
                    NotifiedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EvidenceDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcernNotifications", x => x.Id);
                    table.UniqueConstraint("AK_ConcernNotifications_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ConcernNotifications_NonblankNote", "\"Note\" IS NULL OR btrim(\"Note\") <> ''");
                    table.ForeignKey(
                        name: "FK_ConcernNotifications_CaseConcerns_OrganizationId_EudrCaseId~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId },
                        principalTable: "CaseConcerns",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernNotifications_CaseRecipients_OrganizationId_CaseReci~",
                        columns: x => new { x.OrganizationId, x.CaseRecipientId },
                        principalTable: "CaseRecipients",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernNotifications_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernNotifications_Documents_OrganizationId_EudrCaseId_Ev~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.EvidenceDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernNotifications_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConcernResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcernId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    Rationale = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ResolvedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcernResolutions", x => x.Id);
                    table.UniqueConstraint("AK_ConcernResolutions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ConcernResolutions_NonblankRationale", "btrim(\"Rationale\") <> ''");
                    table.ForeignKey(
                        name: "FK_ConcernResolutions_CaseConcerns_OrganizationId_EudrCaseId_C~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ConcernId },
                        principalTable: "CaseConcerns",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernResolutions_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConcernResolutions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseConcerns_EudrCaseId_Number",
                table: "CaseConcerns",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConcernActions_ConcernId",
                table: "ConcernActions",
                column: "ConcernId");

            migrationBuilder.CreateIndex(
                name: "IX_ConcernActions_OrganizationId_EudrCaseId_ConcernId",
                table: "ConcernActions",
                columns: new[] { "OrganizationId", "EudrCaseId", "ConcernId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcernActions_OrganizationId_EudrCaseId_EvidenceDocumentId",
                table: "ConcernActions",
                columns: new[] { "OrganizationId", "EudrCaseId", "EvidenceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcernNotifications_ConcernId",
                table: "ConcernNotifications",
                column: "ConcernId");

            migrationBuilder.CreateIndex(
                name: "IX_ConcernNotifications_OrganizationId_CaseRecipientId",
                table: "ConcernNotifications",
                columns: new[] { "OrganizationId", "CaseRecipientId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcernNotifications_OrganizationId_EudrCaseId_ConcernId",
                table: "ConcernNotifications",
                columns: new[] { "OrganizationId", "EudrCaseId", "ConcernId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcernNotifications_OrganizationId_EudrCaseId_EvidenceDocu~",
                table: "ConcernNotifications",
                columns: new[] { "OrganizationId", "EudrCaseId", "EvidenceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConcernResolutions_ConcernId",
                table: "ConcernResolutions",
                column: "ConcernId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConcernResolutions_OrganizationId_EudrCaseId_ConcernId",
                table: "ConcernResolutions",
                columns: new[] { "OrganizationId", "EudrCaseId", "ConcernId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses once concern records exist (like the earlier retention migrations): the four tables are retained history.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "CaseConcerns")
                        OR EXISTS (SELECT 1 FROM "ConcernActions")
                        OR EXISTS (SELECT 1 FROM "ConcernNotifications")
                        OR EXISTS (SELECT 1 FROM "ConcernResolutions") THEN
                        RAISE EXCEPTION 'Concern records exist; rollback would destroy history';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "ConcernActions");

            migrationBuilder.DropTable(
                name: "ConcernNotifications");

            migrationBuilder.DropTable(
                name: "ConcernResolutions");

            migrationBuilder.DropTable(
                name: "CaseConcerns");
        }
    }
}
