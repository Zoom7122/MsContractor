using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentsInOperationResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "documents_in_operation",
                schema: "catalog_sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MergeJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_documents_in_operation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_documents_in_operation_merge_jobs_MergeJobId_AccountId",
                        columns: x => new { x.MergeJobId, x.AccountId },
                        principalSchema: "catalog_sync",
                        principalTable: "merge_jobs",
                        principalColumns: new[] { "Id", "AccountId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_documents_in_operation_AccountId_MergeJobId_OperationType",
                schema: "catalog_sync",
                table: "documents_in_operation",
                columns: new[] { "AccountId", "MergeJobId", "OperationType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_documents_in_operation_MergeJobId_AccountId",
                schema: "catalog_sync",
                table: "documents_in_operation",
                columns: new[] { "MergeJobId", "AccountId" });

            migrationBuilder.Sql("""
                ALTER TABLE catalog_sync.documents_in_operation ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.documents_in_operation FORCE ROW LEVEL SECURITY;
                CREATE POLICY account_isolation ON catalog_sync.documents_in_operation
                    USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                    WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "documents_in_operation",
                schema: "catalog_sync");
        }
    }
}
