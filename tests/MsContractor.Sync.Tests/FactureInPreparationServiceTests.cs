using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Facturein;

namespace MsContractor.Sync.Tests;

public sealed class FactureInPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_RequestsAndSavesBatchesOf1000_AndReturnsMissingIdsAsSkipped()
    {
        var accountId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 2001).Select(_ => Guid.NewGuid()).ToArray();
        var gateway = new RecordingGateway();
        var repository = new RecordingRepository();
        var service = new FactureInPreparationService(
            gateway,
            repository,
            NullLogger<FactureInPreparationService>.Instance);

        var result = await service.PrepareAsync(accountId, ids, CancellationToken.None);

        Assert.Equal([1000, 1000, 1], gateway.BatchSizes);
        Assert.Equal(3, repository.Batches.Count);
        Assert.Equal(ids.Length - 3, result.Documents.Count);
        Assert.Equal([ids[0], ids[1000], ids[2000]], result.SkippedDocuments.Select(item => item.DocumentId));
        Assert.All(repository.Batches, batch => Assert.Equal(accountId, batch.AccountId));
    }

    [Fact]
    public async Task PrepareAsync_PersistsRawDataWithCompositeAccountDocumentKey()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var repository = new FactureInRawDataRepository(dbContext);
        await repository.UpsertAsync(
            accountId,
            new Dictionary<Guid, string> { [documentId] = "{\"version\":1}" },
            CancellationToken.None);
        await repository.UpsertAsync(
            otherAccountId,
            new Dictionary<Guid, string> { [documentId] = "{\"version\":2}" },
            CancellationToken.None);

        var rows = await dbContext.FactureInRawData
            .OrderBy(item => item.AccountId)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            new[] { accountId, otherAccountId }.OrderBy(id => id),
            rows.Select(item => item.AccountId));
        Assert.Equal("{\"version\":1}", rows.Single(item => item.AccountId == accountId).RawJson);
        Assert.Equal("{\"version\":2}", rows.Single(item => item.AccountId == otherAccountId).RawJson);
    }

    [Fact]
    public async Task PrepareAsync_HonorsCancellationBeforeTheNextBatch()
    {
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        using var cancellation = new CancellationTokenSource();
        var gateway = new RecordingGateway
        {
            AfterFirstBatch = () => cancellation.Cancel()
        };
        var service = new FactureInPreparationService(
            gateway,
            new RecordingRepository(),
            NullLogger<FactureInPreparationService>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.PrepareAsync(
            Guid.NewGuid(), ids, cancellation.Token));
        Assert.Single(gateway.BatchSizes);
    }

    private sealed class RecordingGateway : IMoySkladFactureInGateway
    {
        public List<int> BatchSizes { get; } = [];
        public Action? AfterFirstBatch { get; init; }

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            IReadOnlyList<Guid> factureInIds,
            CancellationToken cancellationToken)
        {
            BatchSizes.Add(factureInIds.Count);
            var returned = factureInIds
                .Where((_, index) => index != 0)
                .ToDictionary(id => id, id => $"{{\"id\":\"{id:D}\"}}");
            if (BatchSizes.Count == 1)
                AfterFirstBatch?.Invoke();
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(returned);
        }
    }

    private sealed class RecordingRepository : IFactureInRawDataRepository
    {
        public List<(Guid AccountId, IReadOnlyDictionary<Guid, string> Documents)> Batches { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> documentIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, string> documents,
            CancellationToken cancellationToken)
        {
            Batches.Add((accountId, documents));
            return Task.CompletedTask;
        }
    }
}
