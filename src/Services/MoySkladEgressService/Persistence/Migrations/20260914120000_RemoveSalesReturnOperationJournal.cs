using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MsContractor.MoySkladEgressService.Persistence;

#nullable disable

namespace MsContractor.MoySkladEgressService.Persistence.Migrations;

[DbContext(typeof(EgressDbContext))]
[Migration("20260914120000_RemoveSalesReturnOperationJournal")]
public partial class RemoveSalesReturnOperationJournal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "salesreturn_claims", schema: "egress");
        migrationBuilder.DropTable(name: "salesreturn_operations", schema: "egress");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "salesreturn_operations",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                MainCounterpartyId = table.Column<Guid>(type: "uuid", nullable: false),
                CorrelationId = table.Column<string>(type: "text", nullable: false),
                RequestJson = table.Column<string>(type: "jsonb", nullable: false),
                Fingerprint = table.Column<string>(type: "text", nullable: false),
                ItemsJson = table.Column<string>(type: "jsonb", nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_salesreturn_operations", x => new { x.AccountId, x.OperationId }));

        migrationBuilder.CreateTable(
            name: "salesreturn_claims",
            schema: "egress",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                OldDocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_salesreturn_claims", x => new { x.AccountId, x.OldDocumentId });
                table.ForeignKey(
                    name: "FK_salesreturn_claims_salesreturn_operations_AccountId_OperationId",
                    columns: x => new { x.AccountId, x.OperationId },
                    principalSchema: "egress",
                    principalTable: "salesreturn_operations",
                    principalColumns: new[] { "AccountId", "OperationId" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_claims_AccountId_OperationId",
            schema: "egress",
            table: "salesreturn_claims",
            columns: new[] { "AccountId", "OperationId" });
        migrationBuilder.CreateIndex(
            name: "IX_salesreturn_operations_NextAttemptAt",
            schema: "egress",
            table: "salesreturn_operations",
            column: "NextAttemptAt");
    }
}
