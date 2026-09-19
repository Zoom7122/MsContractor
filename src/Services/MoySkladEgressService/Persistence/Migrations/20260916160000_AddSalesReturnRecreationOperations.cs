using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260916160000_AddSalesReturnRecreationOperations")]
public partial class AddSalesReturnRecreationOperations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "salesreturn_recreation_operations",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                MainAgentId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey(
                "PK_salesreturn_recreation_operations", x => new { x.AccountId, x.OperationId }));

        migrationBuilder.CreateTable(
            name: "salesreturn_recreation_items",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                DemandId = table.Column<Guid>(type: "uuid", nullable: true),
                TargetAgentAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                NewSyncId = table.Column<Guid>(type: "uuid", nullable: false),
                RollbackSyncId = table.Column<Guid>(type: "uuid", nullable: false),
                NewDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                RollbackDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                Stage = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true),
                SourceRawJson = table.Column<string>(type: "jsonb", nullable: false),
                NewPayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                RollbackPayloadJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_recreation_items",
                    x => new { x.AccountId, x.OperationId, x.SourceDocumentId });
                table.ForeignKey(
                    name: "FK_salesreturn_recreation_items_salesreturn_recreation_operations_AccountId_OperationId",
                    columns: x => new { x.AccountId, x.OperationId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_recreation_operations",
                    principalColumns: new[] { "AccountId", "OperationId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_recreation_items_AccountId_OperationId",
            schema: "egress",
            table: "salesreturn_recreation_items",
            columns: new[] { "AccountId", "OperationId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "salesreturn_recreation_items", schema: "egress");
        migrationBuilder.DropTable(name: "salesreturn_recreation_operations", schema: "egress");
    }
}
