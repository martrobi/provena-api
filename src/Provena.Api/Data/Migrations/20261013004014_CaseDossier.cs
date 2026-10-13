using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaseDossier : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ProductionPlots_OrganizationId_ProducerId_Id",
                table: "ProductionPlots",
                columns: new[] { "OrganizationId", "ProducerId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PlotRevisions_OrganizationId_ProductionPlotId_Id",
                table: "PlotRevisions",
                columns: new[] { "OrganizationId", "ProductionPlotId", "Id" });

            migrationBuilder.CreateTable(
                name: "CaseDossiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EudrCaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseDossiers", x => x.Id);
                    table.UniqueConstraint("AK_CaseDossiers_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseDossiers_Cases_OrganizationId_EudrCaseId",
                        columns: x => new { x.OrganizationId, x.EudrCaseId },
                        principalTable: "Cases",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseDossiers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CaseLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseDossierId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    LotReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseLots", x => x.Id);
                    table.UniqueConstraint("AK_CaseLots_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_CaseLots_CaseDossiers_OrganizationId_CaseDossierId",
                        columns: x => new { x.OrganizationId, x.CaseDossierId },
                        principalTable: "CaseDossiers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CaseLots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LotContributions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseLotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    Unit = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotContributions", x => x.Id);
                    table.UniqueConstraint("AK_LotContributions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.UniqueConstraint("AK_LotContributions_OrganizationId_Id_ProducerId", x => new { x.OrganizationId, x.Id, x.ProducerId });
                    table.ForeignKey(
                        name: "FK_LotContributions_CaseLots_OrganizationId_CaseLotId",
                        columns: x => new { x.OrganizationId, x.CaseLotId },
                        principalTable: "CaseLots",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LotContributions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LotContributions_Producers_OrganizationId_ProducerId",
                        columns: x => new { x.OrganizationId, x.ProducerId },
                        principalTable: "Producers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContributionPlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LotContributionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductionPlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlotRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductionStartDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProductionEndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContributionPlots", x => x.Id);
                    table.UniqueConstraint("AK_ContributionPlots_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_ContributionPlots_LotContributions_OrganizationId_LotContri~",
                        columns: x => new { x.OrganizationId, x.LotContributionId, x.ProducerId },
                        principalTable: "LotContributions",
                        principalColumns: new[] { "OrganizationId", "Id", "ProducerId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContributionPlots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContributionPlots_PlotRevisions_OrganizationId_ProductionPl~",
                        columns: x => new { x.OrganizationId, x.ProductionPlotId, x.PlotRevisionId },
                        principalTable: "PlotRevisions",
                        principalColumns: new[] { "OrganizationId", "ProductionPlotId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContributionPlots_ProductionPlots_OrganizationId_ProducerId~",
                        columns: x => new { x.OrganizationId, x.ProducerId, x.ProductionPlotId },
                        principalTable: "ProductionPlots",
                        principalColumns: new[] { "OrganizationId", "ProducerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseDossiers_EudrCaseId",
                table: "CaseDossiers",
                column: "EudrCaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseDossiers_OrganizationId_EudrCaseId",
                table: "CaseDossiers",
                columns: new[] { "OrganizationId", "EudrCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseLots_CaseDossierId",
                table: "CaseLots",
                column: "CaseDossierId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaseLots_CaseDossierId_Number",
                table: "CaseLots",
                columns: new[] { "CaseDossierId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaseLots_OrganizationId_CaseDossierId",
                table: "CaseLots",
                columns: new[] { "OrganizationId", "CaseDossierId" });

            migrationBuilder.CreateIndex(
                name: "IX_CaseLots_OrganizationId_LotReference",
                table: "CaseLots",
                columns: new[] { "OrganizationId", "LotReference" },
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ContributionPlots_LotContributionId_ProductionPlotId",
                table: "ContributionPlots",
                columns: new[] { "LotContributionId", "ProductionPlotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContributionPlots_OrganizationId_LotContributionId_Producer~",
                table: "ContributionPlots",
                columns: new[] { "OrganizationId", "LotContributionId", "ProducerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContributionPlots_OrganizationId_ProducerId_ProductionPlotId",
                table: "ContributionPlots",
                columns: new[] { "OrganizationId", "ProducerId", "ProductionPlotId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContributionPlots_OrganizationId_ProductionPlotId_PlotRevis~",
                table: "ContributionPlots",
                columns: new[] { "OrganizationId", "ProductionPlotId", "PlotRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LotContributions_CaseLotId_ProducerId",
                table: "LotContributions",
                columns: new[] { "CaseLotId", "ProducerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LotContributions_OrganizationId_CaseLotId",
                table: "LotContributions",
                columns: new[] { "OrganizationId", "CaseLotId" });

            migrationBuilder.CreateIndex(
                name: "IX_LotContributions_OrganizationId_ProducerId",
                table: "LotContributions",
                columns: new[] { "OrganizationId", "ProducerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback guard: this must stay the first operation. Dropping the tables would destroy case dossier history.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""CaseDossiers"")
       OR EXISTS (SELECT 1 FROM ""CaseLots"")
       OR EXISTS (SELECT 1 FROM ""LotContributions"")
       OR EXISTS (SELECT 1 FROM ""ContributionPlots"")
    THEN
        RAISE EXCEPTION 'Case dossier records exist; rollback would destroy history';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "ContributionPlots");

            migrationBuilder.DropTable(
                name: "LotContributions");

            migrationBuilder.DropTable(
                name: "CaseLots");

            migrationBuilder.DropTable(
                name: "CaseDossiers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ProductionPlots_OrganizationId_ProducerId_Id",
                table: "ProductionPlots");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_PlotRevisions_OrganizationId_ProductionPlotId_Id",
                table: "PlotRevisions");
        }
    }
}
