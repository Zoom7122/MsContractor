using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Repo.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260821000200_AddCounterpartyDocuments")]
public sealed class AddCounterpartyDocuments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "counterparty_documents",
            schema: "catalog_sync",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                CounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_counterparty_documents", x => new { x.AccountId, x.DocumentType, x.DocumentId });
            });
        migrationBuilder.CreateIndex(
            name: "IX_counterparty_documents_AccountId_CounterpartyId",
            schema: "catalog_sync",
            table: "counterparty_documents",
            columns: new[] { "AccountId", "CounterpartyId" });
        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.counterparty_documents ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.counterparty_documents FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.counterparty_documents
                USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "counterparty_documents", schema: "catalog_sync");
    }
}
