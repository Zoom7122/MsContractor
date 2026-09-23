using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260923120000_AddFactureInRawData")]
public partial class AddFactureInRawData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "facturein_raw_data",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                RawJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_facturein_raw_data",
                    x => new { x.AccountId, x.DocumentId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "facturein_raw_data",
            schema: "egress");
    }
}
