using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260904000200_RenameDocumentAdditionalTables")]
public sealed class RenameDocumentAdditionalTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS account_isolation ON catalog_sync.additional_data_commission;
            DROP POLICY IF EXISTS account_isolation ON catalog_sync.additional_data_salesreturn;
            """);

        migrationBuilder.RenameTable(
            name: "additional_data_commission",
            schema: "catalog_sync",
            newName: "document_additional_commission",
            newSchema: "catalog_sync");

        migrationBuilder.RenameTable(
            name: "additional_data_salesreturn",
            schema: "catalog_sync",
            newName: "document_additional_data",
            newSchema: "catalog_sync");

        migrationBuilder.RenameColumn(
            name: "Data",
            schema: "catalog_sync",
            table: "document_additional_data",
            newName: "RawJson");

        migrationBuilder.Sql("""
            ALTER TABLE catalog_sync.document_additional_commission
                RENAME CONSTRAINT "PK_additional_data_commission"
                TO "PK_document_additional_commission";
            ALTER TABLE catalog_sync.document_additional_commission
                RENAME CONSTRAINT "FK_additional_data_commission_counterparty_documents_DocumentId"
                TO "FK_document_additional_commission_counterparty_documents_DocumentId";
            ALTER TABLE catalog_sync.document_additional_data
                RENAME CONSTRAINT "PK_additional_data_salesreturn"
                TO "PK_document_additional_data";
            ALTER TABLE catalog_sync.document_additional_data
                RENAME CONSTRAINT "FK_additional_data_salesreturn_counterparty_documents_DocumentId"
                TO "FK_document_additional_data_counterparty_documents_DocumentId";

            CREATE POLICY account_isolation ON catalog_sync.document_additional_commission
                USING (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.document_additional_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.document_additional_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));

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

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP POLICY IF EXISTS account_isolation ON catalog_sync.document_additional_commission;
            DROP POLICY IF EXISTS account_isolation ON catalog_sync.document_additional_data;

            ALTER TABLE catalog_sync.document_additional_commission
                RENAME CONSTRAINT "PK_document_additional_commission"
                TO "PK_additional_data_commission";
            ALTER TABLE catalog_sync.document_additional_commission
                RENAME CONSTRAINT "FK_document_additional_commission_counterparty_documents_DocumentId"
                TO "FK_additional_data_commission_counterparty_documents_DocumentId";
            ALTER TABLE catalog_sync.document_additional_data
                RENAME CONSTRAINT "PK_document_additional_data"
                TO "PK_additional_data_salesreturn";
            ALTER TABLE catalog_sync.document_additional_data
                RENAME CONSTRAINT "FK_document_additional_data_counterparty_documents_DocumentId"
                TO "FK_additional_data_salesreturn_counterparty_documents_DocumentId";
            """);

        migrationBuilder.RenameColumn(
            name: "RawJson",
            schema: "catalog_sync",
            table: "document_additional_data",
            newName: "Data");

        migrationBuilder.RenameTable(
            name: "document_additional_commission",
            schema: "catalog_sync",
            newName: "additional_data_commission",
            newSchema: "catalog_sync");

        migrationBuilder.RenameTable(
            name: "document_additional_data",
            schema: "catalog_sync",
            newName: "additional_data_salesreturn",
            newSchema: "catalog_sync");

        migrationBuilder.Sql("""
            CREATE POLICY account_isolation ON catalog_sync.additional_data_commission
                USING (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_commission."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));

            CREATE POLICY account_isolation ON catalog_sync.additional_data_salesreturn
                USING (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_salesreturn."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ))
                WITH CHECK (EXISTS (
                    SELECT 1 FROM catalog_sync.counterparty_documents AS document
                    WHERE document."DocumentId" = catalog_sync.additional_data_salesreturn."DocumentId"
                      AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                ));
            """);
    }
}
