using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Repo.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260807000100_AddMergeJobs")]
public sealed class AddMergeJobs : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "merge_jobs",
            schema: "catalog_sync",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                MainCounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                PayloadVersion = table.Column<int>(type: "integer", nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_merge_jobs", x => x.Id);
                table.UniqueConstraint("AK_merge_jobs_Id_AccountId", x => new { x.Id, x.AccountId });
            });

        migrationBuilder.CreateTable(
            name: "merge_operations",
            schema: "catalog_sync",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                MergeJobId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ErrorMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_merge_operations", x => x.Id);
                table.ForeignKey(
                    name: "FK_merge_operations_merge_jobs_MergeJobId_AccountId",
                    columns: x => new { x.MergeJobId, x.AccountId },
                    principalSchema: "catalog_sync",
                    principalTable: "merge_jobs",
                    principalColumns: new[] { "Id", "AccountId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_merge_jobs_AccountId_CreatedAt",
            schema: "catalog_sync", table: "merge_jobs",
            columns: new[] { "AccountId", "CreatedAt" });
        migrationBuilder.CreateIndex(
            name: "IX_merge_jobs_AccountId_Status",
            schema: "catalog_sync", table: "merge_jobs",
            columns: new[] { "AccountId", "Status" });
        migrationBuilder.CreateIndex(
            name: "IX_merge_jobs_MessageId",
            schema: "catalog_sync", table: "merge_jobs",
            column: "MessageId", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_merge_operations_MergeJobId_OperationType_CounterpartyId",
            schema: "catalog_sync", table: "merge_operations",
            columns: new[] { "MergeJobId", "OperationType", "CounterpartyId" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_merge_operations_MergeJobId_AccountId",
            schema: "catalog_sync", table: "merge_operations",
            columns: new[] { "MergeJobId", "AccountId" });
        migrationBuilder.CreateIndex(
            name: "IX_merge_operations_MergeJobId_Sequence",
            schema: "catalog_sync", table: "merge_operations",
            columns: new[] { "MergeJobId", "Sequence" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "merge_operations", schema: "catalog_sync");
        migrationBuilder.DropTable(name: "merge_jobs", schema: "catalog_sync");
    }
}
