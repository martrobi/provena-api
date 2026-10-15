using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaseLifecycleV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReviewRecords_OrganizationId_EudrCaseId_Id",
                table: "ReviewRecords",
                columns: new[] { "OrganizationId", "EudrCaseId", "Id" });

            migrationBuilder.CreateTable(
                name: "CaseArchivals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ArchivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    UnarchivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UnarchivedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UnarchiveReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseArchivals", x => x.Id);
                    table.UniqueConstraint("AK_CaseArchivals_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseArchivals_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseArchivals_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseClosures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    ReviewRecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalDdsReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseClosures", x => x.Id);
                    table.UniqueConstraint("AK_CaseClosures_OrganizationId_EudrCaseId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.Id });
                    table.UniqueConstraint("AK_CaseClosures_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseClosures_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseClosures_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseClosures_ReviewRecords_OrganizationId_EudrCaseId_Review~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ReviewRecordId },
                        principalTable: "ReviewRecords",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseReopenings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseClosureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ReopenedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReopenedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseReopenings", x => x.Id);
                    table.UniqueConstraint("AK_CaseReopenings_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseReopenings_CaseClosures_OrganizationId_EudrCaseId_CaseC~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.CaseClosureId },
                        principalTable: "CaseClosures",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseReopenings_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseReopenings_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseArchivals_EudrCaseId",
                table: "CaseArchivals",
                column: "EudrCaseId",
                unique: true,
                filter: "\"UnarchivedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseArchivals_EudrCaseId_Number",
                table: "CaseArchivals",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseArchivals_OrganizationId_EudrCaseId",
                table: "CaseArchivals",
                columns: new[] { "OrganizationId", "EudrCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_EudrCaseId_Number",
                table: "CaseClosures",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_OrganizationId_EudrCaseId_ReviewRecordId",
                table: "CaseClosures",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReviewRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseClosures_ReviewRecordId",
                table: "CaseClosures",
                column: "ReviewRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseReopenings_CaseClosureId",
                table: "CaseReopenings",
                column: "CaseClosureId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseReopenings_OrganizationId_EudrCaseId_CaseClosureId",
                table: "CaseReopenings",
                columns: new[] { "OrganizationId", "EudrCaseId", "CaseClosureId" });

            // Data conversion (local development data; kept to what is needed to be safe and truthful). It runs after the new tables exist and before the legacy
            // columns are dropped, in this order because each step reads values the next one changes. No closure and no reopening is invented: a v2 closure needs an
            // Owner review that legacy Completed cases never had, and no Owner reopen occurred.

            // 1. A legacy completion (Completed status, or a completion timestamp on a case that was later archived) is invalidated. The old completion time and DDS
            //    reference are preserved as System audit evidence before the columns are dropped.
            migrationBuilder.Sql("""
                INSERT INTO "AuditEvents" ("Id", "CreatedAtUtc", "UpdatedAtUtc", "OrganizationId", "EudrCaseId", "ActorType", "ActorUserId", "SupplierRequestId", "EventType", "Description", "MetadataJson")
                SELECT gen_random_uuid(), now(), now(), c."OrganizationId", c."Id", 2, NULL, NULL, 'LegacyCompletionInvalidatedByMigration',
                       'Case lifecycle v2 migration: the legacy completion is not a v2 closure because no qualifying Owner review exists. The case was not closed and its status was recalculated to an open status.',
                       jsonb_build_object('legacyStatus', CASE c."Status" WHEN 4 THEN 'Completed' WHEN 5 THEN 'Archived' ELSE c."Status"::text END,
                                          'legacyCompletedAtUtc', c."CompletedAtUtc",
                                          'legacyExternalDdsReference', c."ExternalDdsReference")
                FROM "Cases" c
                WHERE c."Status" = 4 OR c."CompletedAtUtc" IS NOT NULL;
                """);

            // 2. The legacy Archived status becomes the independent archive history (the workflow status is recalculated in step 3).
            migrationBuilder.Sql("""
                INSERT INTO "CaseArchivals" ("Id", "CreatedAtUtc", "UpdatedAtUtc", "OrganizationId", "EudrCaseId", "Number", "ArchivedAtUtc", "ArchivedByUserId", "Reason", "UnarchivedAtUtc", "UnarchivedByUserId", "UnarchiveReason")
                SELECT gen_random_uuid(), now(), now(), c."OrganizationId", c."Id", 1, c."UpdatedAtUtc", NULL, 'Archived before case lifecycle v2.', NULL, NULL, NULL
                FROM "Cases" c
                WHERE c."Status" = 5;
                """);

            // 3. Former Completed (4 = Closed now) and Archived (5) rows get the open workflow status, mirroring EudrCaseStatusService.CalculateOpen
            //    (0 Draft, 1 WaitingForSupplier, 2 ReviewNeeded, 3 ReadyForExport; requirement 0 Missing, 1 Submitted, 2 Accepted, 3 Rejected, 4 NotRequired).
            migrationBuilder.Sql("""
                UPDATE "Cases" c SET "Status" = x."Next",
                    "InternalReviewCompletedAtUtc" = CASE WHEN x."Next" = 3 THEN COALESCE(c."InternalReviewCompletedAtUtc", now()) ELSE NULL END
                FROM (
                    SELECT k."Id", CASE
                        WHEN NOT EXISTS (SELECT 1 FROM "SupplierRequests" r WHERE r."EudrCaseId" = k."Id" AND r."SentAtUtc" IS NOT NULL) THEN 0
                        WHEN EXISTS (SELECT 1 FROM "Requirements" q WHERE q."EudrCaseId" = k."Id" AND q."IsRequired" AND q."Status" = 0) THEN 1
                        WHEN EXISTS (SELECT 1 FROM "Requirements" q WHERE q."EudrCaseId" = k."Id" AND q."Status" IN (1, 3)) THEN 2
                        WHEN NOT EXISTS (SELECT 1 FROM "Requirements" q WHERE q."EudrCaseId" = k."Id" AND q."IsRequired" AND q."Status" NOT IN (2, 4)) THEN 3
                        ELSE 1 END AS "Next"
                    FROM "Cases" k WHERE k."Status" IN (4, 5)) x
                WHERE c."Id" = x."Id";
                """);

            migrationBuilder.DropColumn(
                name: "CompletedAtUtc",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "ExternalDdsReference",
                table: "Cases");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses once lifecycle history exists (like the earlier migrations). There is no reverse data conversion: the legacy columns come back empty and the
            // preserved legacy values stay in the LegacyCompletionInvalidatedByMigration audit events.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "CaseClosures") OR EXISTS (SELECT 1 FROM "CaseReopenings") OR EXISTS (SELECT 1 FROM "CaseArchivals") THEN
                        RAISE EXCEPTION 'Case lifecycle records exist; rollback would destroy history';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "CaseArchivals");

            migrationBuilder.DropTable(
                name: "CaseReopenings");

            migrationBuilder.DropTable(
                name: "CaseClosures");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ReviewRecords_OrganizationId_EudrCaseId_Id",
                table: "ReviewRecords");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAtUtc",
                table: "Cases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalDdsReference",
                table: "Cases",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);
        }
    }
}
