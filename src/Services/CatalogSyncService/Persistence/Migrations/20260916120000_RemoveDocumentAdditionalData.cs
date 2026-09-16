using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.CatalogSyncService.Persistence;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260916120000_RemoveDocumentAdditionalData")]
public sealed class RemoveDocumentAdditionalData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "document_additional_data",
            schema: "catalog_sync");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "document_additional_data",
            schema: "catalog_sync",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_document_additional_data", x => x.DocumentId);
                table.ForeignKey(
                    name: "FK_document_additional_data_counterparty_documents_DocumentId",
                    column: x => x.DocumentId,
                    principalSchema: "catalog_sync",
                    principalTable: "counterparty_documents",
                    principalColumn: "DocumentId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.document_additional_data ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.document_additional_data FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.document_additional_data
                USING (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.document_additional_data."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.document_additional_data."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));
            """);
    }
}
