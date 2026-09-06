using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistSalesReturnRecreationRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SalesReturnRequestJson",
                schema: "catalog_sync",
                table: "merge_operations",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SalesReturnRequestJson",
                schema: "catalog_sync",
                table: "merge_operations");
        }
    }
}
