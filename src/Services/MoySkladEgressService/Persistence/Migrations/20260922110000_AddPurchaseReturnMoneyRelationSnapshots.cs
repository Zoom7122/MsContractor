using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260922110000_AddPurchaseReturnMoneyRelationSnapshots")]
public partial class AddPurchaseReturnMoneyRelationSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "OperationsBeforeJson",
            schema: "egress",
            table: "purchasereturn_paymentin_raw_data",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "LinkedSum",
            schema: "egress",
            table: "purchasereturn_paymentin_raw_data",
            type: "numeric",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OperationsBeforeJson",
            schema: "egress",
            table: "purchasereturn_cashin_raw_data",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<decimal>(
            name: "LinkedSum",
            schema: "egress",
            table: "purchasereturn_cashin_raw_data",
            type: "numeric",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "OperationsBeforeJson",
            schema: "egress",
            table: "purchasereturn_paymentin_raw_data");

        migrationBuilder.DropColumn(
            name: "LinkedSum",
            schema: "egress",
            table: "purchasereturn_paymentin_raw_data");

        migrationBuilder.DropColumn(
            name: "OperationsBeforeJson",
            schema: "egress",
            table: "purchasereturn_cashin_raw_data");

        migrationBuilder.DropColumn(
            name: "LinkedSum",
            schema: "egress",
            table: "purchasereturn_cashin_raw_data");
    }
}
