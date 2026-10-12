using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Provena.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductionProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Producers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Producers", x => x.Id);
                    table.UniqueConstraint("AK_Producers_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_Producers_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionPlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProducerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionPlots", x => x.Id);
                    table.UniqueConstraint("AK_ProductionPlots_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_ProductionPlots_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionPlots_Producers_OrganizationId_ProducerId",
                        columns: x => new { x.OrganizationId, x.ProducerId },
                        principalTable: "Producers",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlotRevisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductionPlotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CountryCode = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    GeometryType = table.Column<int>(type: "integer", nullable: false),
                    GeometryJson = table.Column<string>(type: "jsonb", nullable: false),
                    AreaHectares = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: true),
                    Note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupersededAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotRevisions", x => x.Id);
                    table.UniqueConstraint("AK_PlotRevisions_OrganizationId_Id", x => new { x.OrganizationId, x.Id });
                    table.ForeignKey(
                        name: "FK_PlotRevisions_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlotRevisions_ProductionPlots_OrganizationId_ProductionPlot~",
                        columns: x => new { x.OrganizationId, x.ProductionPlotId },
                        principalTable: "ProductionPlots",
                        principalColumns: new[] { "OrganizationId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlotRevisions_OrganizationId_ProductionPlotId",
                table: "PlotRevisions",
                columns: new[] { "OrganizationId", "ProductionPlotId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlotRevisions_ProductionPlotId",
                table: "PlotRevisions",
                column: "ProductionPlotId",
                unique: true,
                filter: "\"SupersededAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PlotRevisions_ProductionPlotId_Number",
                table: "PlotRevisions",
                columns: new[] { "ProductionPlotId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Producers_OrganizationId_ExternalReference",
                table: "Producers",
                columns: new[] { "OrganizationId", "ExternalReference" },
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionPlots_OrganizationId_ExternalReference",
                table: "ProductionPlots",
                columns: new[] { "OrganizationId", "ExternalReference" },
                unique: true,
                filter: "\"ExternalReference\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionPlots_OrganizationId_ProducerId",
                table: "ProductionPlots",
                columns: new[] { "OrganizationId", "ProducerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback guard: this must stay the first operation. Dropping the tables would destroy production provenance history.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM ""Producers"")
       OR EXISTS (SELECT 1 FROM ""ProductionPlots"")
       OR EXISTS (SELECT 1 FROM ""PlotRevisions"")
    THEN
        RAISE EXCEPTION 'Production provenance records exist; rollback would destroy history';
    END IF;
END $$;");

            migrationBuilder.DropTable(
                name: "PlotRevisions");

            migrationBuilder.DropTable(
                name: "ProductionPlots");

            migrationBuilder.DropTable(
                name: "Producers");
        }
    }
}
