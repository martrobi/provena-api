using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class SourceQuantityAllocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AllocatedQuantity",
                table: "ContributionPlots",
                type: "numeric(20,6)",
                precision: 20,
                scale: 6,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ContributionPlots_AllocatedQuantityPositive",
                table: "ContributionPlots",
                sql: "\"AllocatedQuantity\" IS NULL OR \"AllocatedQuantity\" > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Refuses while any plot link carries a recorded allocation (retained history; dropping the column would erase it). Legacy rows (null) never block a rollback.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "ContributionPlots" WHERE "AllocatedQuantity" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Source allocations exist; rollback would destroy history';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ContributionPlots_AllocatedQuantityPositive",
                table: "ContributionPlots");

            migrationBuilder.DropColumn(
                name: "AllocatedQuantity",
                table: "ContributionPlots");
        }
    }
}
