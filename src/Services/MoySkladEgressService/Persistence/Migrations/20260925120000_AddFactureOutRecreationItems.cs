using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260925120000_AddFactureOutRecreationItems")]
public partial class AddFactureOutRecreationItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "factureout_recreation_items",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSyncId = table.Column<Guid>(type: "uuid", nullable: true),
                NewSyncId = table.Column<Guid>(type: "uuid", nullable: true),
                NewDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                Status = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_factureout_recreation_items",
                    x => new { x.AccountId, x.SourceDocumentId });
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "factureout_recreation_items", schema: "egress");
}
