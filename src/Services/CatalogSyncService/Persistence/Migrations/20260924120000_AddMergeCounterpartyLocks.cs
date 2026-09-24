using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260924120000_AddMergeCounterpartyLocks")]
public sealed class AddMergeCounterpartyLocks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "merge_counterparty_locks",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                MergeJobId = table.Column<Guid>(type: "uuid", nullable: false),
                AcquiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_merge_counterparty_locks", x => new { x.AccountId, x.CounterpartyId });
                table.ForeignKey(
                    name: "FK_merge_counterparty_locks_merge_jobs_MergeJobId_AccountId",
                    columns: x => new { x.MergeJobId, x.AccountId },
                    principalSchema: "catalog_sync",
                    principalTable: "merge_jobs",
                    principalColumns: new[] { "Id", "AccountId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_merge_counterparty_locks_MergeJobId_AccountId",
            schema: "catalog_sync",
            table: "merge_counterparty_locks",
            columns: new[] { "MergeJobId", "AccountId" });

        // Migration has no tenant context. Temporarily lift RLS inside the migration transaction
        // so all accounts' pending jobs are backfilled, then restore it before commit.
        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.merge_jobs DISABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.merge_operations DISABLE ROW LEVEL SECURITY;
            """);

        // Existing queued/running jobs must own their participants before new requests are accepted.
        // A collision here is unsafe to resolve automatically, so the unique key stops deployment.
        migrationBuilder.Sql("""
            INSERT INTO catalog_sync.merge_counterparty_locks
                ("AccountId", "CounterpartyId", "MergeJobId", "AcquiredAt")
            SELECT participant."AccountId", participant."CounterpartyId", participant."MergeJobId", participant."CreatedAt"
            FROM (
                SELECT job."AccountId", job."MainCounterpartyId" AS "CounterpartyId",
                       job."Id" AS "MergeJobId", job."CreatedAt"
                FROM catalog_sync.merge_jobs AS job
                WHERE job."Status" IN ('pending', 'running')
                UNION
                SELECT job."AccountId", operation."CounterpartyId", job."Id", job."CreatedAt"
                FROM catalog_sync.merge_jobs AS job
                JOIN catalog_sync.merge_operations AS operation
                  ON operation."MergeJobId" = job."Id" AND operation."AccountId" = job."AccountId"
                WHERE job."Status" IN ('pending', 'running')
                  AND operation."OperationType" = 'archive_duplicate'
            ) AS participant;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.merge_jobs ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.merge_jobs FORCE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.merge_operations ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.merge_operations FORCE ROW LEVEL SECURITY;
            """);

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.merge_counterparty_locks ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.merge_counterparty_locks FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.merge_counterparty_locks
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "merge_counterparty_locks", schema: "catalog_sync");
    }
}
