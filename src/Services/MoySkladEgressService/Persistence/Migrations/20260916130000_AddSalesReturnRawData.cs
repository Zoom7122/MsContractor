using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260916130000_AddSalesReturnRawData")]
public partial class AddSalesReturnRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "salesreturn_raw_data",
            schema: "egress",
            columns: table => new
            {
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_salesreturn_raw_data", x => x.DocumentId);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "salesreturn_raw_data",
            schema: "egress");
    }
}
