using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260916140000_AddAccountIdToSalesReturnRawData")]
public partial class AddAccountIdToSalesReturnRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "AccountId",
            schema: "egress",
            table: "salesreturn_raw_data",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.DropPrimaryKey(
            name: "PK_salesreturn_raw_data",
            schema: "egress",
            table: "salesreturn_raw_data");

        migrationBuilder.AddPrimaryKey(
            name: "PK_salesreturn_raw_data",
            schema: "egress",
            table: "salesreturn_raw_data",
            columns: new[] { "AccountId", "DocumentId" });

        migrationBuilder.AlterColumn<Guid>(
            name: "AccountId",
            schema: "egress",
            table: "salesreturn_raw_data",
            type: "uuid",
            nullable: false,
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldDefaultValue: Guid.Empty);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropPrimaryKey(
            name: "PK_salesreturn_raw_data",
            schema: "egress",
            table: "salesreturn_raw_data");

        migrationBuilder.DropColumn(
            name: "AccountId",
            schema: "egress",
            table: "salesreturn_raw_data");

        migrationBuilder.AddPrimaryKey(
            name: "PK_salesreturn_raw_data",
            schema: "egress",
            table: "salesreturn_raw_data",
            column: "DocumentId");
    }
}
