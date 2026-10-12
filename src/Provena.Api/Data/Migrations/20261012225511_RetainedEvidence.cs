using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RetainedEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Submissions_EudrCaseId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Geolocations_EudrCaseId",
                table: "Geolocations");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAtUtc",
                table: "Submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierRequestId",
                table: "Submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SupersededAtUtc",
                table: "Geolocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByUserId",
                table: "Geolocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByUserId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawnAtUtc",
                table: "Documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WithdrawnByUserId",
                table: "Documents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_EudrCaseId",
                table: "Submissions",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Geolocations_EudrCaseId",
                table: "Geolocations",
                column: "EudrCaseId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback guard: this must stay the first operation. Dropping the columns and restoring the full unique
            // indexes would destroy or reject retained evidence history, so refuse explicitly instead of relying on
            // the index creation happening to fail.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Geolocations"" GROUP BY ""EudrCaseId"" HAVING COUNT(*) > 1)
       OR EXISTS (SELECT 1 FROM ""Submissions"" GROUP BY ""EudrCaseId"" HAVING COUNT(*) > 1)
       OR EXISTS (SELECT 1 FROM ""Geolocations"" WHERE ""SupersededAtUtc"" IS NOT NULL)
       OR EXISTS (SELECT 1 FROM ""Submissions"" WHERE ""SupersededAtUtc"" IS NOT NULL)
       OR EXISTS (SELECT 1 FROM ""Documents"" WHERE ""WithdrawnAtUtc"" IS NOT NULL)
    THEN
        RAISE EXCEPTION 'Retained evidence versions exist; rollback would destroy history';
    END IF;
END $$;");

            migrationBuilder.DropIndex(
                name: "IX_Submissions_EudrCaseId",
                table: "Submissions");

            migrationBuilder.DropIndex(
                name: "IX_Geolocations_EudrCaseId",
                table: "Geolocations");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "SupplierRequestId",
                table: "Submissions");

            migrationBuilder.DropColumn(
                name: "SupersededAtUtc",
                table: "Geolocations");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "Geolocations");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "WithdrawnAtUtc",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "WithdrawnByUserId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_EudrCaseId",
                table: "Submissions",
                column: "EudrCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Geolocations_EudrCaseId",
                table: "Geolocations",
                column: "EudrCaseId",
                unique: true);
        }
    }
}
