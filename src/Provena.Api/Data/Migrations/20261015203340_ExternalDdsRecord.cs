using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExternalDdsRecord : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalDdsRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    ReviewId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VerificationNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SubmittedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersedesExternalDdsRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReplacementReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalDdsRecords", x => x.Id);
                    table.UniqueConstraint("AK_ExternalDdsRecords_OrganizationId_EudrCaseId_Id", x => new { x.OrganizationId, x.EudrCaseId, x.Id });
                    table.UniqueConstraint("AK_ExternalDdsRecords_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.CheckConstraint("CK_ExternalDdsRecords_NonblankNumbers", "btrim(\"ReferenceNumber\") <> '' AND btrim(\"VerificationNumber\") <> ''");
                    table.CheckConstraint("CK_ExternalDdsRecords_ReplacementPair", "(\"SupersedesExternalDdsRecordId\" IS NULL AND \"ReplacementReason\" IS NULL) OR (\"SupersedesExternalDdsRecordId\" IS NOT NULL AND \"ReplacementReason\" IS NOT NULL AND btrim(\"ReplacementReason\") <> '')");
                    table.ForeignKey(
                        name: "FK_ExternalDdsRecords_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalDdsRecords_Documents_OrganizationId_EudrCaseId_Rece~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ReceiptDocumentId },
                        principalTable: "Documents",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalDdsRecords_ExternalDdsRecords_OrganizationId_EudrCa~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.SupersedesExternalDdsRecordId },
                        principalTable: "ExternalDdsRecords",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalDdsRecords_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExternalDdsRecords_ReviewRecords_OrganizationId_EudrCaseId_~",
                        columns: x => new { x.OrganizationId, x.EudrCaseId, x.ReviewId },
                        principalTable: "ReviewRecords",
                        principalColumns: new[] { "OrganizationId", "EudrCaseId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_EudrCaseId",
                table: "ExternalDdsRecords",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_EudrCaseId_Number",
                table: "ExternalDdsRecords",
                columns: new[] { "EudrCaseId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_OrganizationId_EudrCaseId_ReceiptDocumen~",
                table: "ExternalDdsRecords",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReceiptDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_OrganizationId_EudrCaseId_ReviewId",
                table: "ExternalDdsRecords",
                columns: new[] { "OrganizationId", "EudrCaseId", "ReviewId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_OrganizationId_EudrCaseId_SupersedesExte~",
                table: "ExternalDdsRecords",
                columns: new[] { "OrganizationId", "EudrCaseId", "SupersedesExternalDdsRecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_ReviewId",
                table: "ExternalDdsRecords",
                column: "ReviewId");

            migrationBuilder.CreateIndex(
                name: "IX_ExternalDdsRecords_SupersedesExternalDdsRecordId",
                table: "ExternalDdsRecords",
                column: "SupersedesExternalDdsRecordId",
                unique: true,
                filter: "\"SupersedesExternalDdsRecordId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses once External DDS records exist (like the earlier retention migrations): the table is retained history.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ExternalDdsRecords") THEN
                        RAISE EXCEPTION 'External DDS records exist; rollback would destroy history';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "ExternalDdsRecords");
        }
    }
}
