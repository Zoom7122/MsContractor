using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Repo.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260805000100_AddIncrementalCounterpartySync")]
public sealed class AddIncrementalCounterpartySync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ExecutionMode",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "full");

        migrationBuilder.AddColumn<string>(
            name: "RequestedMode",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "full");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "WindowFrom",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "WindowTo",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "sync_watermarks",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Watermark = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSyncRunId = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_sync_watermarks", x => x.AccountId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "sync_watermarks",
            schema: "catalog_sync");

        migrationBuilder.DropColumn(name: "ExecutionMode", schema: "catalog_sync", table: "sync_runs");
        migrationBuilder.DropColumn(name: "RequestedMode", schema: "catalog_sync", table: "sync_runs");
        migrationBuilder.DropColumn(name: "WindowFrom", schema: "catalog_sync", table: "sync_runs");
        migrationBuilder.DropColumn(name: "WindowTo", schema: "catalog_sync", table: "sync_runs");
    }
}
