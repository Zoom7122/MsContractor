using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ErrorsCount",
                schema: "catalog_sync",
                table: "sync_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "sync_runs_error",
                schema: "catalog_sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SyncRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CounterpartyId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ErrorKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs_error", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sync_runs_error_sync_runs_SyncRunId_AccountId",
                        columns: x => new { x.SyncRunId, x.AccountId },
                        principalSchema: "catalog_sync",
                        principalTable: "sync_runs",
                        principalColumns: new[] { "Id", "AccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_sync_runs_ErrorsCount",
                schema: "catalog_sync",
                table: "sync_runs",
                sql: "\"ErrorsCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_error_AccountId_SyncRunId_CreatedAt_Id",
                schema: "catalog_sync",
                table: "sync_runs_error",
                columns: new[] { "AccountId", "SyncRunId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_error_AccountId_SyncRunId_ErrorKey",
                schema: "catalog_sync",
                table: "sync_runs_error",
                columns: new[] { "AccountId", "SyncRunId", "ErrorKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_error_SyncRunId_AccountId",
                schema: "catalog_sync",
                table: "sync_runs_error",
                columns: new[] { "SyncRunId", "AccountId" });

            migrationBuilder.Sql("""
                ALTER TABLE catalog_sync.sync_runs_error ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.sync_runs_error FORCE ROW LEVEL SECURITY;
                CREATE POLICY account_isolation ON catalog_sync.sync_runs_error
                    USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                    WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sync_runs_error",
                schema: "catalog_sync");

            migrationBuilder.DropCheckConstraint(
                name: "CK_sync_runs_ErrorsCount",
                schema: "catalog_sync",
                table: "sync_runs");

            migrationBuilder.DropColumn(
                name: "ErrorsCount",
                schema: "catalog_sync",
                table: "sync_runs");
        }
    }
}
