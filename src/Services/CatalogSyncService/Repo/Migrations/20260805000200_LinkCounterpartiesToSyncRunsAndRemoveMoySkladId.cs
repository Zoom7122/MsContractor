using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MsContractor.CatalogSyncService.Repo.Migrations;

[DbContext(typeof(CatalogSyncDbContext))]
[Migration("20260805000200_LinkCounterpartiesToSyncRunsAndRemoveMoySkladId")]
public sealed class LinkCounterpartiesToSyncRunsAndRemoveMoySkladId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_counterparties_AccountId_MoySkladId",
            schema: "catalog_sync",
            table: "counterparties");
        migrationBuilder.Sql("UPDATE catalog_sync.counterparties SET \"Id\" = \"MoySkladId\" WHERE \"Id\" <> \"MoySkladId\";");
        migrationBuilder.DropColumn(
            name: "MoySkladId",
            schema: "catalog_sync",
            table: "counterparties");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_sync_runs_Id_AccountId",
            schema: "catalog_sync",
            table: "sync_runs",
            columns: new[] { "Id", "AccountId" });
        migrationBuilder.CreateIndex(
            name: "IX_counterparties_AccountId_Id",
            schema: "catalog_sync",
            table: "counterparties",
            columns: new[] { "AccountId", "Id" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_counterparties_LastSyncRunId_AccountId",
            schema: "catalog_sync",
            table: "counterparties",
            columns: new[] { "LastSyncRunId", "AccountId" });
        migrationBuilder.AddForeignKey(
            name: "FK_counterparties_sync_runs_LastSyncRunId_AccountId",
            schema: "catalog_sync",
            table: "counterparties",
            columns: new[] { "LastSyncRunId", "AccountId" },
            principalSchema: "catalog_sync",
            principalTable: "sync_runs",
            principalColumns: new[] { "Id", "AccountId" },
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_counterparties_sync_runs_LastSyncRunId_AccountId",
            schema: "catalog_sync",
            table: "counterparties");
        migrationBuilder.DropIndex(
            name: "IX_counterparties_AccountId_Id",
            schema: "catalog_sync",
            table: "counterparties");
        migrationBuilder.DropIndex(
            name: "IX_counterparties_LastSyncRunId_AccountId",
            schema: "catalog_sync",
            table: "counterparties");
        migrationBuilder.DropUniqueConstraint(
            name: "AK_sync_runs_Id_AccountId",
            schema: "catalog_sync",
            table: "sync_runs");
        migrationBuilder.AddColumn<Guid>(
            name: "MoySkladId",
            schema: "catalog_sync",
            table: "counterparties",
            type: "uuid",
            nullable: false,
            defaultValue: Guid.Empty);
        migrationBuilder.Sql("UPDATE catalog_sync.counterparties SET \"MoySkladId\" = \"Id\";");
        migrationBuilder.CreateIndex(
            name: "IX_counterparties_AccountId_MoySkladId",
            schema: "catalog_sync",
            table: "counterparties",
            columns: new[] { "AccountId", "MoySkladId" },
            unique: true);
    }
}
