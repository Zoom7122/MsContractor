using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260921100000_AddSalesReturnRelationSnapshots")]
public partial class AddSalesReturnRelationSnapshots : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RelationsStatus",
            schema: "egress",
            table: "salesreturn_recreation_items",
            type: "text",
            nullable: false,
            defaultValue: "Pending");

        migrationBuilder.CreateTable(
            name: "salesreturn_relation_snapshots",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_relation_snapshots",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId });
                table.ForeignKey(
                    "FK_salesreturn_relation_snapshots_salesreturn_recreation_items_AccountId_OperationId_SourceSalesReturnId",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_recreation_items",
                    principalColumns: new[] { "AccountId", "OperationId", "SourceDocumentId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_relation_snapshots_AccountId_OperationId",
            schema: "egress",
            table: "salesreturn_relation_snapshots",
            columns: new[] { "AccountId", "OperationId" });

        migrationBuilder.CreateTable(
            name: "salesreturn_paymentout_relations",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationsBeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                LinkedSum = table.Column<decimal>(type: "numeric", nullable: false),
                DetachStatus = table.Column<string>(type: "text", nullable: false),
                ReattachStatus = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_paymentout_relations",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId, x.DocumentId });
                table.ForeignKey(
                    "FK_salesreturn_paymentout_relations_salesreturn_relation_snapshots_AccountId_OperationId_SourceSalesReturnId",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_relation_snapshots",
                    principalColumns: new[] { "AccountId", "OperationId", "SourceSalesReturnId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "salesreturn_cashout_relations",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationsBeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                LinkedSum = table.Column<decimal>(type: "numeric", nullable: false),
                DetachStatus = table.Column<string>(type: "text", nullable: false),
                ReattachStatus = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_cashout_relations",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId, x.DocumentId });
                table.ForeignKey(
                    "FK_salesreturn_cashout_relations_salesreturn_relation_snapshots_AccountId_OperationId_SourceSalesReturnId",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_relation_snapshots",
                    principalColumns: new[] { "AccountId", "OperationId", "SourceSalesReturnId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "salesreturn_loss_relations",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                SourceSalesReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                SalesReturnBeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                DetachStatus = table.Column<string>(type: "text", nullable: false),
                ReattachStatus = table.Column<string>(type: "text", nullable: false),
                ErrorCode = table.Column<string>(type: "text", nullable: true),
                Error = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_salesreturn_loss_relations",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId, x.DocumentId });
                table.ForeignKey(
                    "FK_salesreturn_loss_relations_salesreturn_relation_snapshots_AccountId_OperationId_SourceSalesReturnId",
                    x => new { x.AccountId, x.OperationId, x.SourceSalesReturnId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_relation_snapshots",
                    principalColumns: new[] { "AccountId", "OperationId", "SourceSalesReturnId" },
                    onDelete: ReferentialAction.Cascade);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "salesreturn_paymentout_relations", schema: "egress");
        migrationBuilder.DropTable(name: "salesreturn_cashout_relations", schema: "egress");
        migrationBuilder.DropTable(name: "salesreturn_loss_relations", schema: "egress");
        migrationBuilder.DropTable(name: "salesreturn_relation_snapshots", schema: "egress");
        migrationBuilder.DropColumn(
            name: "RelationsStatus",
            schema: "egress",
            table: "salesreturn_recreation_items");
    }
}
