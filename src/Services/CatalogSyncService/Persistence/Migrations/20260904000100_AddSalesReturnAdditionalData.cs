using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260904000100_AddSalesReturnAdditionalData")]
public sealed class AddSalesReturnAdditionalData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS account_isolation
                ON catalog_sync.counterparty_document_additional_data;
            """);

        migrationBuilder.RenameTable(
            name: "counterparty_document_additional_data",
            schema: "catalog_sync",
            newName: "additional_data_commission",
            newSchema: "catalog_sync");

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.additional_data_commission
                RENAME CONSTRAINT "PK_counterparty_document_additional_data"
                TO "PK_additional_data_commission";
            ALTER TABLE catalog_sync.additional_data_commission
                RENAME CONSTRAINT "FK_counterparty_document_additional_data_counterparty_documents_DocumentId"
                TO "FK_additional_data_commission_counterparty_documents_DocumentId";
            CREATE POLICY account_isolation ON catalog_sync.additional_data_commission
                USING (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));
            """);

        migrationBuilder.CreateTable(
            name: "additional_data_salesreturn",
            schema: "catalog_sync",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                Data = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_additional_data_salesreturn", x => x.DocumentId);
                table.ForeignKey(
                    name: "FK_additional_data_salesreturn_counterparty_documents_DocumentId",
                    column: x => x.DocumentId,
                    principalSchema: "catalog_sync",
                    principalTable: "counterparty_documents",
                    principalColumn: "DocumentId",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.additional_data_salesreturn ENABLE ROW LEVEL SECURITY;
            ALTER TABLE catalog_sync.additional_data_salesreturn FORCE ROW LEVEL SECURITY;
            CREATE POLICY account_isolation ON catalog_sync.additional_data_salesreturn
                USING (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_salesreturn."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1
                    FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_salesreturn."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "additional_data_salesreturn",
            schema: "catalog_sync");

        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS account_isolation
                ON catalog_sync.additional_data_commission;
            ALTER TABLE catalog_sync.additional_data_commission
                RENAME CONSTRAINT "PK_additional_data_commission"
                TO "PK_counterparty_document_additional_data";
            ALTER TABLE catalog_sync.additional_data_commission
                RENAME CONSTRAINT "FK_additional_data_commission_counterparty_documents_DocumentId"
                TO "FK_counterparty_document_additional_data_counterparty_documents_DocumentId";
            """);

        migrationBuilder.RenameTable(
            name: "additional_data_commission",
            schema: "catalog_sync",
            newName: "counterparty_document_additional_data",
            newSchema: "catalog_sync");

        migrationBuilder.Sql("""
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
}
