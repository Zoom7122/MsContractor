using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.Sync.Tests;

public sealed class FactureOutPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_SavesPreparedAndSkippedRows_AndResetsExistingState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new EgressDbContext(
            new DbContextOptionsBuilder<EgressDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var sourceSyncId = Guid.NewGuid();
        var oldNewSyncId = Guid.NewGuid();
        var otherAccountSyncId = Guid.NewGuid();
        dbContext.FactureOutRecreationItems.AddRange(
            new FactureOutRecreationItem
            {
                AccountId = accountId,
                SourceDocumentId = documentId,
                NewSyncId = oldNewSyncId,
                NewDocumentId = Guid.NewGuid(),
                Status = FactureOutRecreationStatuses.CreatePayload
            },
            new FactureOutRecreationItem
            {
                AccountId = accountId,
                SourceDocumentId = missingId,
                NewSyncId = Guid.NewGuid(),
                Status = FactureOutRecreationStatuses.CreatePayload
            },
            new FactureOutRecreationItem
            {
                AccountId = otherAccountId,
                SourceDocumentId = documentId,
                NewSyncId = otherAccountSyncId,
                Status = FactureOutRecreationStatuses.CreatePayload
            });
        await dbContext.SaveChangesAsync();

        var rawJson = $$"""
            {"id":"{{documentId:D}}","syncId":"{{sourceSyncId:D}}"}
            """;
        var service = new FactureOutPreparationService(
            new RecordingGateway(new Dictionary<Guid, string> { [documentId] = rawJson }),
            new FactureOutRawDataRepository(dbContext),
            new FactureOutRecreationItemRepository(dbContext),
            NullLogger<FactureOutPreparationService>.Instance);

        var result = await service.PrepareAsync(
            accountId,
            [documentId, missingId],
            CancellationToken.None);

        var prepared = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == accountId && item.SourceDocumentId == documentId);
        Assert.Equal(sourceSyncId, prepared.SourceSyncId);
        Assert.Null(prepared.NewSyncId);
        Assert.Null(prepared.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.Prepared, prepared.Status);

        var skipped = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == accountId && item.SourceDocumentId == missingId);
        Assert.Null(skipped.SourceSyncId);
        Assert.Null(skipped.NewSyncId);
        Assert.Null(skipped.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.Skipped, skipped.Status);

        var otherAccount = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == otherAccountId && item.SourceDocumentId == documentId);
        Assert.Equal(otherAccountSyncId, otherAccount.NewSyncId);
        Assert.Equal(FactureOutRecreationStatuses.CreatePayload, otherAccount.Status);
        Assert.Equal([documentId], result.DocumentIds);
        Assert.Equal(missingId, Assert.Single(result.SkippedDocuments).DocumentId);
    }

    private sealed class RecordingGateway(IReadOnlyDictionary<Guid, string> documents)
        : IMoySkladFactureOutGateway
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                documents.Where(item => factureOutIds.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value));
    }
}
