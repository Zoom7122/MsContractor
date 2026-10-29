using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeDocumentIdentityTenantScoped : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS account_isolation ON catalog_sync.document_additional_commission;
                ALTER TABLE catalog_sync.document_additional_commission NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.document_additional_commission DISABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.counterparty_documents NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.counterparty_documents DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_document_additional_commission_counterparty_documents_Docum~",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.DropPrimaryKey(
                name: "PK_document_additional_commission",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_counterparty_documents_DocumentId",
                schema: "catalog_sync",
                table: "counterparty_documents");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                schema: "catalog_sync",
                table: "document_additional_commission",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentType",
                schema: "catalog_sync",
                table: "document_additional_commission",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE catalog_sync.document_additional_commission AS commission
                SET "AccountId" = document."AccountId",
                    "DocumentType" = document."DocumentType"
                FROM catalog_sync.counterparty_documents AS document
                WHERE document."DocumentId" = commission."DocumentId";
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "AccountId",
                schema: "catalog_sync",
                table: "document_additional_commission",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "DocumentType",
                schema: "catalog_sync",
                table: "document_additional_commission",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_document_additional_commission",
                schema: "catalog_sync",
                table: "document_additional_commission",
                columns: new[] { "AccountId", "DocumentType", "DocumentId" });

            migrationBuilder.AddForeignKey(
                name: "FK_document_additional_commission_counterparty_documents_Accou~",
                schema: "catalog_sync",
                table: "document_additional_commission",
                columns: new[] { "AccountId", "DocumentType", "DocumentId" },
                principalSchema: "catalog_sync",
                principalTable: "counterparty_documents",
                principalColumns: new[] { "AccountId", "DocumentType", "DocumentId" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                ALTER TABLE catalog_sync.document_additional_commission ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.document_additional_commission FORCE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.counterparty_documents ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.counterparty_documents FORCE ROW LEVEL SECURITY;
                CREATE POLICY account_isolation ON catalog_sync.document_additional_commission
                    USING ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid)
                    WITH CHECK ("AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS account_isolation ON catalog_sync.document_additional_commission;
                ALTER TABLE catalog_sync.document_additional_commission NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.document_additional_commission DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_document_additional_commission_counterparty_documents_Accou~",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.DropPrimaryKey(
                name: "PK_document_additional_commission",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_counterparty_documents_DocumentId",
                schema: "catalog_sync",
                table: "counterparty_documents",
                column: "DocumentId");

            migrationBuilder.DropColumn(
                name: "AccountId",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.DropColumn(
                name: "DocumentType",
                schema: "catalog_sync",
                table: "document_additional_commission");

            migrationBuilder.AddPrimaryKey(
                name: "PK_document_additional_commission",
                schema: "catalog_sync",
                table: "document_additional_commission",
                column: "DocumentId");

            migrationBuilder.AddForeignKey(
                name: "FK_document_additional_commission_counterparty_documents_Docum~",
                schema: "catalog_sync",
                table: "document_additional_commission",
                column: "DocumentId",
                principalSchema: "catalog_sync",
                principalTable: "counterparty_documents",
                principalColumn: "DocumentId",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.Sql("""
                ALTER TABLE catalog_sync.document_additional_commission ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog_sync.document_additional_commission FORCE ROW LEVEL SECURITY;
                CREATE POLICY account_isolation ON catalog_sync.document_additional_commission
                    USING (EXISTS (
                        SELECT 1
                        FROM catalog_sync.counterparty_documents AS document
                        WHERE document."DocumentId" = catalog_sync.document_additional_commission."DocumentId"
                          AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    ))
                    WITH CHECK (EXISTS (
                        SELECT 1
                        FROM catalog_sync.counterparty_documents AS document
                        WHERE document."DocumentId" = catalog_sync.document_additional_commission."DocumentId"
                          AND document."AccountId" = NULLIF(current_setting('app.account_id', true), '')::uuid
                    ));
                """);
        }
    }
}
