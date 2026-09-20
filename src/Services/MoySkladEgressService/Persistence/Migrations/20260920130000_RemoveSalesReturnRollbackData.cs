using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260920130000_RemoveSalesReturnRollbackData")]
public partial class RemoveSalesReturnRollbackData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "RollbackDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items");

        migrationBuilder.DropColumn(
            name: "RollbackPayloadJson",
            schema: "egress",
            table: "salesreturn_recreation_items");

        migrationBuilder.DropColumn(
            name: "RollbackSyncId",
            schema: "egress",
            table: "salesreturn_recreation_items");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "RollbackDocumentId",
            schema: "egress",
            table: "salesreturn_recreation_items",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RollbackPayloadJson",
            schema: "egress",
            table: "salesreturn_recreation_items",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");

        migrationBuilder.AddColumn<Guid>(
            name: "RollbackSyncId",
            schema: "egress",
            table: "salesreturn_recreation_items",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);
    }
}
