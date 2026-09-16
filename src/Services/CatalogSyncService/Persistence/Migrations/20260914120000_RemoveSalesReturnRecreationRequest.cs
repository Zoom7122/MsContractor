using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MsContractor.CatalogSyncService.Persistence;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260914120000_RemoveSalesReturnRecreationRequest")]
public partial class RemoveSalesReturnRecreationRequest : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SalesReturnRequestJson",
            schema: "catalog_sync",
            table: "merge_operations");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SalesReturnRequestJson",
            schema: "catalog_sync",
            table: "merge_operations",
            type: "jsonb",
            nullable: true);
    }
}
