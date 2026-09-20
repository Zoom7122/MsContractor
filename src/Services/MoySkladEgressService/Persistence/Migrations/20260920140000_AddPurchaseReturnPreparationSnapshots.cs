using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260920140000_AddPurchaseReturnPreparationSnapshots")]
public partial class AddPurchaseReturnPreparationSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "purchasereturn_raw_data",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_purchasereturn_raw_data", x => new { x.AccountId, x.DocumentId });
            });

        migrationBuilder.CreateTable(
            name: "purchasereturn_positions_raw_data",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                PurchaseReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_purchasereturn_positions_raw_data",
                    x => new { x.AccountId, x.PurchaseReturnId, x.PositionId });
                table.ForeignKey(
                    "FK_purchasereturn_positions_raw_data_purchasereturn_raw_data_AccountId_PurchaseReturnId",
                    x => new { x.AccountId, x.PurchaseReturnId },
                    principalSchema: "egress",
                    principalTable: "purchasereturn_raw_data",
                    principalColumns: new[] { "AccountId", "DocumentId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_purchasereturn_positions_raw_data_AccountId_PurchaseReturnId",
            schema: "egress",
            table: "purchasereturn_positions_raw_data",
            columns: new[] { "AccountId", "PurchaseReturnId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "purchasereturn_positions_raw_data",
            schema: "egress");

        migrationBuilder.DropTable(
            name: "purchasereturn_raw_data",
            schema: "egress");
    }
}
