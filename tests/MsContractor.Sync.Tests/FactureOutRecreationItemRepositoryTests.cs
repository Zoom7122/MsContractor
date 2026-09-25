using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.Sync.Tests;

public sealed class FactureOutRecreationItemRepositoryTests
{
    [Fact]
    public async Task SavePayloadBuildResultsAsync_StoresNewSyncIdsAndSkippedStatuses()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new EgressDbContext(
            new DbContextOptionsBuilder<EgressDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var successfulId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var newSyncId = Guid.NewGuid();
        var repository = new FactureOutRecreationItemRepository(dbContext);
        await repository.UpsertPreparationAsync(
            accountId,
            new Dictionary<Guid, Guid?>
            {
                [successfulId] = Guid.NewGuid(),
                [skippedId] = null
            },
            [],
            CancellationToken.None);

        await repository.SavePayloadBuildResultsAsync(
            accountId,
            new Dictionary<Guid, Guid> { [successfulId] = newSyncId },
            [skippedId],
            CancellationToken.None);

        var successful = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.SourceDocumentId == successfulId);
        Assert.Equal(newSyncId, successful.NewSyncId);
        Assert.Null(successful.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.CreatePayload, successful.Status);

        var skipped = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.SourceDocumentId == skippedId);
        Assert.Null(skipped.NewSyncId);
        Assert.Null(skipped.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.Skipped, skipped.Status);
    }
}
