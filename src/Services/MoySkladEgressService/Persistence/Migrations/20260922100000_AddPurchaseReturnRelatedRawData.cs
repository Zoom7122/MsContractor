using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260922100000_AddPurchaseReturnRelatedRawData")]
public partial class AddPurchaseReturnRelatedRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        CreateRelatedTable(migrationBuilder, "purchasereturn_factureout_raw_data");
        CreateRelatedTable(migrationBuilder, "purchasereturn_facturein_raw_data");
        CreateRelatedTable(migrationBuilder, "purchasereturn_paymentin_raw_data");
        CreateRelatedTable(migrationBuilder, "purchasereturn_cashin_raw_data");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "purchasereturn_cashin_raw_data", schema: "egress");
        migrationBuilder.DropTable(name: "purchasereturn_paymentin_raw_data", schema: "egress");
        migrationBuilder.DropTable(name: "purchasereturn_facturein_raw_data", schema: "egress");
        migrationBuilder.DropTable(name: "purchasereturn_factureout_raw_data", schema: "egress");
    }

    private static void CreateRelatedTable(MigrationBuilder migrationBuilder, string tableName)
    {
        migrationBuilder.CreateTable(
            name: tableName,
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    $"PK_{tableName}",
                    x => new { x.AccountId, x.PurchaseReturnId, x.DocumentId });
                table.ForeignKey(
                    $"FK_{tableName}_purchasereturn_raw_data_AccountId_PurchaseReturnId",
                    x => new { x.AccountId, x.PurchaseReturnId },
                    principalSchema: "egress",
                    principalTable: "purchasereturn_raw_data",
                    principalColumns: new[] { "AccountId", "DocumentId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: $"IX_{tableName}_AccountId_PurchaseReturnId",
            schema: "egress",
            table: tableName,
            columns: new[] { "AccountId", "PurchaseReturnId" });
    }
}
