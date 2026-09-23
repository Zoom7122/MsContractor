using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260923130000_AddFactureInRecreationItems")]
public partial class AddFactureInRecreationItems : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "facturein_recreation_items",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceFactureInId = table.Column<Guid>(type: "uuid", nullable: false),
                MainCounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSyncId = table.Column<Guid>(type: "uuid", nullable: true),
                NewSyncId = table.Column<Guid>(type: "uuid", nullable: false),
                NewFactureInId = table.Column<Guid>(type: "uuid", nullable: true),
                PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                Stage = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_facturein_recreation_items", x => new { x.AccountId, x.SourceFactureInId });
                table.ForeignKey(
                    name: "FK_facturein_recreation_items_facturein_raw_data_AccountId_SourceFactureInId",
                    columns: x => new { x.AccountId, x.SourceFactureInId },
                    principalSchema: "egress",
                    principalTable: "facturein_raw_data",
                    principalColumns: new[] { "AccountId", "DocumentId" },
                    onDelete: ReferentialAction.Restrict);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "facturein_recreation_items", schema: "egress");
}
