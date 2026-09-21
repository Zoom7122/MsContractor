using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_SavesSnapshots_LoadsDistinctSupplies_AndReturnsReadyAndSkippedDocuments()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var readyId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var readySupplyId = Guid.NewGuid();
        var skippedSupplyId = Guid.NewGuid();
        var events = new List<string>();
        var documents = new Dictionary<Guid, string>
        {
            [readyId] = Document(readySupplyId),
            [skippedId] = Document(skippedSupplyId)
        };
        var positions = new RecordingPositionsGateway(new Dictionary<Guid, MoySkladPurchaseReturnPositionsPage>
        {
            [readyId] = Page(readyId),
            [skippedId] = Page(skippedId)
        });
        var repository = new RecordingRepository(events);
        var supplies = new RecordingSupplyGateway(
            new Dictionary<Guid, MoySkladSupplyReference>
            {
                [readySupplyId] = new(readySupplyId, mainCounterpartyId),
                [skippedSupplyId] = new(skippedSupplyId, Guid.NewGuid())
            },
            events);
        var service = new PurchaseReturnPreparationService(
            new RecordingPurchaseReturnGateway(documents, events),
            positions,
            supplies,
            repository);

        var result = await service.PrepareAsync(
            accountId,
            mainCounterpartyId,
            [readyId, skippedId, missingId],
            CancellationToken.None);

        Assert.Equal([readyId], result.ReadyForRecreationIds);
        Assert.Contains(result.Skipped, item => item.PurchaseReturnId == skippedId &&
            item.Reason.Contains("does not match", StringComparison.Ordinal));
        Assert.Contains(result.Skipped, item => item.PurchaseReturnId == missingId);
        Assert.Equal([readySupplyId, skippedSupplyId], supplies.RequestedSupplyIds);
        Assert.Equal([readyId, skippedId], repository.SavedPositionDocumentIds);
        Assert.Equal(["documents", "positions", "supplies"], events.Where(item =>
            item is "documents" or "positions" or "supplies"));
    }

    [Fact]
    public async Task PrepareAsync_SkipsDocumentWhenPositionsAreMissing()
    {
        var purchaseReturnId = Guid.NewGuid();
        var repository = new RecordingRepository([]);
        var service = new PurchaseReturnPreparationService(
            new RecordingPurchaseReturnGateway(
                new Dictionary<Guid, string> { [purchaseReturnId] = Document(Guid.NewGuid()) }, []),
            new RecordingPositionsGateway(new Dictionary<Guid, MoySkladPurchaseReturnPositionsPage>
            {
                [purchaseReturnId] = new(0, 1000, 0, new Dictionary<Guid, string>())
            }),
            new RecordingSupplyGateway(new Dictionary<Guid, MoySkladSupplyReference>(), []),
            repository);

        var result = await service.PrepareAsync(
            Guid.NewGuid(), Guid.NewGuid(), [purchaseReturnId], CancellationToken.None);

        Assert.Empty(result.ReadyForRecreationIds);
        Assert.Contains(result.Skipped, item => item.PurchaseReturnId == purchaseReturnId &&
            item.Reason.Contains("no positions", StringComparison.Ordinal));
        Assert.Empty(repository.SavedPositionDocumentIds);
    }

    [Fact]
    public async Task PrepareAsync_AllowsDocumentWithoutSupply()
    {
        var purchaseReturnId = Guid.NewGuid();
        var events = new List<string>();
        var repository = new RecordingRepository(events);
        var supplies = new RecordingSupplyGateway(new Dictionary<Guid, MoySkladSupplyReference>(), events);
        var service = new PurchaseReturnPreparationService(
            new RecordingPurchaseReturnGateway(
                new Dictionary<Guid, string> { [purchaseReturnId] = DocumentWithoutSupply() }, events),
            new RecordingPositionsGateway(new Dictionary<Guid, MoySkladPurchaseReturnPositionsPage>
            {
                [purchaseReturnId] = Page(purchaseReturnId)
            }),
            supplies,
            repository);

        var result = await service.PrepareAsync(
            Guid.NewGuid(), Guid.NewGuid(), [purchaseReturnId], CancellationToken.None);

        Assert.Equal([purchaseReturnId], result.ReadyForRecreationIds);
        Assert.Empty(result.Skipped);
        Assert.Empty(supplies.RequestedSupplyIds);
        Assert.DoesNotContain("supplies", events);
        Assert.Equal([purchaseReturnId], repository.SavedPositionDocumentIds);
    }

    [Fact]
    public async Task Repository_ReplacePositions_ReplacesRowsAndPreservesAccountScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new PurchaseReturnPreparationRepository(dbContext);
        var accountId = Guid.NewGuid();
        var purchaseReturnId = Guid.NewGuid();
        dbContext.PurchaseReturnRawData.Add(new PurchaseReturnRawData
        {
            AccountId = accountId,
            DocumentId = purchaseReturnId,
            RawJson = Document(Guid.NewGuid())
        });
        await dbContext.SaveChangesAsync();

        var positionId = Guid.NewGuid();
        await repository.ReplacePositionsAsync(
            accountId,
            purchaseReturnId,
            new Dictionary<Guid, string> { [positionId] = "{\"version\":1}" },
            CancellationToken.None);
        await repository.ReplacePositionsAsync(
            accountId,
            purchaseReturnId,
            new Dictionary<Guid, string> { [positionId] = "{\"version\":2}" },
            CancellationToken.None);

        var saved = await dbContext.PurchaseReturnPositionRawData.SingleAsync();
        Assert.Equal(accountId, saved.AccountId);
        Assert.Equal(purchaseReturnId, saved.PurchaseReturnId);
        Assert.Equal("{\"version\":2}", saved.RawJson);
    }

    private static string Document(Guid supplyId) =>
        JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid(),
            supply = new
            {
                meta = new
                {
                    href = $"https://api.moysklad.ru/api/remap/1.2/entity/supply/{supplyId:D}"
                }
            }
        });

    private static string DocumentWithoutSupply() =>
        JsonSerializer.Serialize(new
        {
            id = Guid.NewGuid()
        });

    private static MoySkladPurchaseReturnPositionsPage Page(Guid documentId)
    {
        var positionId = Guid.NewGuid();
        return new MoySkladPurchaseReturnPositionsPage(
            1,
            1000,
            0,
            new Dictionary<Guid, string>
            {
                [positionId] = JsonSerializer.Serialize(new { id = positionId, quantity = 1 })
            });
    }

    private sealed class RecordingPurchaseReturnGateway(
        IReadOnlyDictionary<Guid, string> documents,
        List<string> events) : IMoySkladPurchaseReturnGateway
    {
        public Task<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>> GetAgentAccountsAsync(
            Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>>([]);

        public Task<IReadOnlyList<MoySkladPurchaseReturnContract>> GetContractsAsync(
            Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnContract>>([]);

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            IReadOnlyList<Guid> purchaseReturnIds,
            CancellationToken cancellationToken)
        {
            events.Add("documents");
            return Task.FromResult(documents);
        }

        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingPositionsGateway(
        IReadOnlyDictionary<Guid, MoySkladPurchaseReturnPositionsPage> pages) : IMoySkladPurchaseReturnPositionsGateway
    {
        public Task<MoySkladPurchaseReturnPositionsPage> GetPageAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            Guid purchaseReturnId,
            int limit,
            int offset,
            CancellationToken cancellationToken) =>
            Task.FromResult(pages[purchaseReturnId]);
    }

    private sealed class RecordingSupplyGateway(
        IReadOnlyDictionary<Guid, MoySkladSupplyReference> supplies,
        List<string> events) : IMoySkladSupplyGateway
    {
        public IReadOnlyList<Guid> RequestedSupplyIds { get; private set; } = [];

        public Task<IReadOnlyDictionary<Guid, MoySkladSupplyReference>> GetAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            IReadOnlyList<Guid> supplyIds,
            CancellationToken cancellationToken)
        {
            RequestedSupplyIds = supplyIds;
            events.Add("supplies");
            return Task.FromResult(supplies);
        }
    }

    private sealed class RecordingRepository(List<string> events) : IPurchaseReturnPreparationRepository
    {
        public List<Guid> SavedPositionDocumentIds { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task UpsertDocumentsAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, string> documents,
            CancellationToken cancellationToken)
        {
            events.Add("positions");
            return Task.CompletedTask;
        }

        public Task ReplacePositionsAsync(
            Guid accountId,
            Guid purchaseReturnId,
            IReadOnlyDictionary<Guid, string> positions,
            CancellationToken cancellationToken)
        {
            SavedPositionDocumentIds.Add(purchaseReturnId);
            return Task.CompletedTask;
        }
    }
}
