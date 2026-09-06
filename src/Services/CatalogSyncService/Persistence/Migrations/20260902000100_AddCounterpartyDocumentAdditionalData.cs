using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260902000100_AddCounterpartyDocumentAdditionalData")]
public sealed class AddCounterpartyDocumentAdditionalData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddUniqueConstraint(
            name: "AK_counterparty_documents_DocumentId",
            schema: "catalog_sync",
            table: "counterparty_documents",
            column: "DocumentId");

        migrationBuilder.CreateTable(
            name: "counterparty_document_additional_data",
            schema: "catalog_sync",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                Contract = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_counterparty_document_additional_data", x => x.DocumentId);
                table.ForeignKey(
                    name: "FK_counterparty_document_additional_data_counterparty_documents_DocumentId",
                    column: x => x.DocumentId,
                    principalSchema: "catalog_sync",
                    principalTable: "counterparty_documents",
                    principalColumn: "DocumentId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.counterparty_document_additional_data ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.counterparty_document_additional_data FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.counterparty_document_additional_data
                USING (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.counterparty_document_additional_data."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.counterparty_document_additional_data."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "counterparty_document_additional_data",
            schema: "catalog_sync");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_counterparty_documents_DocumentId",
            schema: "catalog_sync",
            table: "counterparty_documents");
    }
}
