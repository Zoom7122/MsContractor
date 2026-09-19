using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260916150000_AddSalesReturnPositionsRawData")]
public partial class AddSalesReturnPositionsRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "salesreturn_positions_raw_data",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                SalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                PositionId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_positions_raw_data",
                    x => new { x.AccountId, x.SalesReturnId, x.PositionId });
                table.ForeignKey(
                    name: "FK_salesreturn_positions_raw_data_salesreturn_raw_data_AccountId_SalesReturnId",
                    columns: x => new { x.AccountId, x.SalesReturnId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_raw_data",
                    principalColumns: new[] { "AccountId", "DocumentId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_positions_raw_data_AccountId_SalesReturnId",
            schema: "egress",
            table: "salesreturn_positions_raw_data",
            columns: new[] { "AccountId", "SalesReturnId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "salesreturn_positions_raw_data",
            schema: "egress");
    }
}
