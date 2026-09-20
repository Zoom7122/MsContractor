using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260920120000_LinkSalesReturnRecreationItemsToRawData")]
public partial class LinkSalesReturnRecreationItemsToRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO egress.salesreturn_raw_data ("AccountId", "DocumentId", "RawJson")
            SELECT DISTINCT ON (item."AccountId", item."SourceDocumentId")
                item."AccountId", item."SourceDocumentId", item."SourceRawJson"
            FROM egress.salesreturn_recreation_items AS item
            LEFT JOIN egress.salesreturn_raw_data AS raw
                ON raw."AccountId" = item."AccountId"
                AND raw."DocumentId" = item."SourceDocumentId"
            WHERE raw."AccountId" IS NULL
            ORDER BY item."AccountId", item."SourceDocumentId", item."OperationId";
            """);

        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_recreation_items_AccountId_SourceDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items",
            columns: new[] { "AccountId", "SourceDocumentId" });

        migrationBuilder.AddForeignKey(
            name: "FK_salesreturn_recreation_items_salesreturn_raw_data_AccountId_SourceDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items",
            columns: new[] { "AccountId", "SourceDocumentId" },
            principalSchema: "egress",
            principalTable: "salesreturn_raw_data",
            principalColumns: new[] { "AccountId", "DocumentId" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_salesreturn_recreation_items_salesreturn_raw_data_AccountId_SourceDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items");

        migrationBuilder.DropIndex(
            name: "IX_salesreturn_recreation_items_AccountId_SourceDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items");
    }
}
