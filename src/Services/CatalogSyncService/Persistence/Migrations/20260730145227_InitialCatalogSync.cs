using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalogSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog_sync");

            migrationBuilder.CreateTable(
                name: "counterparties",
                schema: "catalog_sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    MoySkladId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Phone = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Inn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Kpp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    NormalizedPhone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    NormalizedInn = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    NormalizedKpp = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MoySkladUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    RawJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_counterparties", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "catalog_sync",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox_messages", x => new { x.MessageId, x.ConsumerName });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "catalog_sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EventType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishAttempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sync_runs",
                schema: "catalog_sync",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProcessedCount = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sync_runs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_counterparties_AccountId_MoySkladId",
                schema: "catalog_sync",
                table: "counterparties",
                columns: new[] { "AccountId", "MoySkladId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_counterparties_AccountId_NormalizedEmail",
                schema: "catalog_sync",
                table: "counterparties",
                columns: new[] { "AccountId", "NormalizedEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_counterparties_AccountId_NormalizedInn",
                schema: "catalog_sync",
                table: "counterparties",
                columns: new[] { "AccountId", "NormalizedInn" });

            migrationBuilder.CreateIndex(
                name: "IX_counterparties_AccountId_NormalizedName",
                schema: "catalog_sync",
                table: "counterparties",
                columns: new[] { "AccountId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_counterparties_AccountId_NormalizedPhone",
                schema: "catalog_sync",
                table: "counterparties",
                columns: new[] { "AccountId", "NormalizedPhone" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_PublishedAt_CreatedAt",
                schema: "catalog_sync",
                table: "outbox_messages",
                columns: new[] { "PublishedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_AccountId_CreatedAt",
                schema: "catalog_sync",
                table: "sync_runs",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_AccountId_Status",
                schema: "catalog_sync",
                table: "sync_runs",
                columns: new[] { "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sync_runs_MessageId",
                schema: "catalog_sync",
                table: "sync_runs",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "counterparties",
                schema: "catalog_sync");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "catalog_sync");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "catalog_sync");

            migrationBuilder.DropTable(
                name: "sync_runs",
                schema: "catalog_sync");
        }
    }
}
