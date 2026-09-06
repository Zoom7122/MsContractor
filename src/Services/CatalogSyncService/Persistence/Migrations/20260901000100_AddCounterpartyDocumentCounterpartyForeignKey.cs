using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260901000100_AddCounterpartyDocumentCounterpartyForeignKey")]
public sealed class AddCounterpartyDocumentCounterpartyForeignKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_counterparty_documents_CounterpartyId_AccountId",
            schema: "catalog_sync",
            table: "counterparty_documents",
            columns: new[] { "CounterpartyId", "AccountId" });

        migrationBuilder.AddForeignKey(
            name: "FK_counterparty_documents_counterparties_CounterpartyId_AccountId",
            schema: "catalog_sync",
            table: "counterparty_documents",
            columns: new[] { "CounterpartyId", "AccountId" },
            principalSchema: "catalog_sync",
            principalTable: "counterparties",
            principalColumns: new[] { "Id", "AccountId" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_counterparty_documents_counterparties_CounterpartyId_AccountId",
            schema: "catalog_sync",
            table: "counterparty_documents");

        migrationBuilder.DropIndex(
            name: "IX_counterparty_documents_CounterpartyId_AccountId",
            schema: "catalog_sync",
            table: "counterparty_documents");
    }
}
