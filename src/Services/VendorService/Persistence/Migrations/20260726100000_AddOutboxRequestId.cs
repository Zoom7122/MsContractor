using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.VendorService.Persistence.Migrations;

[DbContext(typeof(VendorDbContext))]
[Migration("20260726100000_AddOutboxRequestId")]
public partial class AddOutboxRequestId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "request_id",
            schema: "vendor",
            table: "outbox_messages",
            type: "varchar(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.CreateIndex(
            name: "ux_outbox_messages_request_id",
            schema: "vendor",
            table: "outbox_messages",
            column: "request_id",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_outbox_messages_request_id",
            schema: "vendor",
            table: "outbox_messages");

        migrationBuilder.DropColumn(
            name: "request_id",
            schema: "vendor",
            table: "outbox_messages");
    }
}
