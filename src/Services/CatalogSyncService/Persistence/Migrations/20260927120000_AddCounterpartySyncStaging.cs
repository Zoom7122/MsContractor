using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260927120000_AddCounterpartySyncStaging")]
public sealed class AddCounterpartySyncStaging : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "ProcessingOwnerToken",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "ProcessingLeaseExpiresAtTicks",
            schema: "catalog_sync",
            table: "sync_runs",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "counterparty_sync_staging",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                SyncRunId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                CounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                IsValidForStorage = table.Column<bool>(type: "boolean", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Phone = table.Column<string>(type: "text", nullable: true),
                Email = table.Column<string>(type: "text", nullable: true),
                Inn = table.Column<string>(type: "text", nullable: true),
                Kpp = table.Column<string>(type: "text", nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                Archived = table.Column<bool>(type: "boolean", nullable: false),
                NormalizedName = table.Column<string>(type: "text", nullable: false),
                NormalizedPhone = table.Column<string>(type: "text", nullable: true),
                NormalizedEmail = table.Column<string>(type: "text", nullable: true),
                NormalizedInn = table.Column<string>(type: "text", nullable: true),
                NormalizedKpp = table.Column<string>(type: "text", nullable: true),
                MoySkladUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                MoySkladUpdatedSortValue = table.Column<long>(type: "bigint", nullable: false),
                LastSyncRunId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_counterparty_sync_staging", x => new { x.AccountId, x.SyncRunId, x.Sequence });
                table.ForeignKey(
                    name: "FK_counterparty_sync_staging_sync_runs_SyncRunId_AccountId",
                    columns: x => new { x.SyncRunId, x.AccountId },
                    principalSchema: "catalog_sync",
                    principalTable: "sync_runs",
                    principalColumns: new[] { "Id", "AccountId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_counterparty_sync_staging_SyncRunId_AccountId",
            schema: "catalog_sync",
            table: "counterparty_sync_staging",
            columns: new[] { "SyncRunId", "AccountId" });

        migrationBuilder.CreateIndex(
            name: "IX_counterparty_sync_staging_AccountId_SyncRunId_CounterpartyId_Sequence",
            schema: "catalog_sync",
            table: "counterparty_sync_staging",
            columns: new[] { "AccountId", "SyncRunId", "CounterpartyId", "Sequence" });

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.counterparty_sync_staging ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.counterparty_sync_staging FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.counterparty_sync_staging
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "counterparty_sync_staging", schema: "catalog_sync");

        migrationBuilder.DropColumn(name: "ProcessingOwnerToken", schema: "catalog_sync", table: "sync_runs");
        migrationBuilder.DropColumn(name: "ProcessingLeaseExpiresAtTicks", schema: "catalog_sync", table: "sync_runs");
    }
}
