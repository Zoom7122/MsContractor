using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.VendorService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CreateVendorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "vendor");

            migrationBuilder.CreateTable(
                name: "installations",
                schema: "vendor",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_uid = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false),
                    account_name = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true),
                    status = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    access_token_ciphertext = table.Column<byte[]>(type: "bytea", nullable: true),
                    access_token_nonce = table.Column<byte[]>(type: "bytea", nullable: true),
                    access_token_tag = table.Column<byte[]>(type: "bytea", nullable: true),
                    token_key_version = table.Column<int>(type: "integer", nullable: true),
                    access_scope = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    subscription = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_context_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_installations", x => x.account_id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "vendor",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    publish_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_installations_app_id",
                schema: "vendor",
                table: "installations",
                column: "app_id");

            migrationBuilder.CreateIndex(
                name: "ix_installations_status",
                schema: "vendor",
                table: "installations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_installations_updated_at",
                schema: "vendor",
                table: "installations",
                column: "updated_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_account_id",
                schema: "vendor",
                table: "outbox_messages",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_created_at",
                schema: "vendor",
                table: "outbox_messages",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_unpublished",
                schema: "vendor",
                table: "outbox_messages",
                columns: new[] { "published_at", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "fk_outbox_messages_installation",
                schema: "vendor",
                table: "outbox_messages",
                column: "account_id",
                principalSchema: "vendor",
                principalTable: "installations",
                principalColumn: "account_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "vendor");

            migrationBuilder.DropTable(
                name: "installations",
                schema: "vendor");
        }
    }
}
