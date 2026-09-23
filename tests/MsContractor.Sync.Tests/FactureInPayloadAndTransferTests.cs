using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Facturein;

namespace MsContractor.Sync.Tests;

public sealed class FactureInPayloadBuilderTests
{
    [Fact]
    public async Task BuildAsync_UsesRawJsonRemovesTechnicalFieldsAndPersistsStableSyncId()
    {
        var accountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var supplyId = Guid.NewGuid();
        var sourceSyncId = Guid.NewGuid();
        var raw = $$$"""
            {"id":"{{{documentId:D}}}","meta":{"type":"facturein"},"accountId":"a","created":"now","updated":"now","deleted":null,"printed":false,"published":false,"files":[],"sum":10,"syncId":"{{{sourceSyncId:D}}}","contract":{"meta":{"type":"contract"}},"agent":{"meta":{"href":"https://api.test/api/remap/1.2/entity/counterparty/{{{Guid.NewGuid():D}}}","type":"counterparty"}},"organization":{"meta":{"href":"https://api.test/api/remap/1.2/entity/organization/{{{Guid.NewGuid():D}}}","type":"organization"}},"supplies":[{"meta":{"href":"https://api.test/api/remap/1.2/entity/supply/{{{supplyId:D}}}","type":"supply"}}],"customField":{"kept":true}}
            """;
        var rawData = new RawDataRepository(documentId, raw);
        var items = new ItemRepository();
        var gateway = new BaseGateway(new HashSet<FactureInBaseReference> { new("supply", supplyId) });
        var builder = new FactureInPayloadBuilder(rawData, items, gateway);

        var result = await builder.BuildAsync(accountId, mainCounterpartyId, [documentId], CancellationToken.None);

        var item = Assert.Single(result.PreparedDocuments);
        Assert.Equal(sourceSyncId, item.SourceSyncId);
        Assert.NotEqual(Guid.Empty, item.NewSyncId);
        var payload = JsonNode.Parse(item.PayloadJson)!.AsObject();
        foreach (var field in new[] { "meta", "id", "accountId", "created", "updated", "deleted", "printed", "published", "files", "sum", "contract" })
            Assert.False(payload.ContainsKey(field));
        Assert.Equal(item.NewSyncId.ToString("D"), payload["syncId"]!.GetValue<string>());
        Assert.True(payload["customField"]! ["kept"]!.GetValue<bool>());
        Assert.EndsWith($"/entity/counterparty/{mainCounterpartyId:D}", payload["agent"]!["meta"]!["href"]!.GetValue<string>());
        Assert.NotNull(payload["organization"]);
        Assert.NotNull(payload["supplies"]);
        Assert.Empty(result.SkippedDocuments);

        var retry = await builder.BuildAsync(accountId, mainCounterpartyId, [documentId], CancellationToken.None);
        Assert.Equal(item.NewSyncId, Assert.Single(retry.PreparedDocuments).NewSyncId);
        Assert.Single(items.Created);
    }

    [Fact]
    public async Task BuildAsync_SkipsUnsupportedOrMissingBasesBeforeTransfer()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var baseId = Guid.NewGuid();
        var raw = new RawDataRepository(new Dictionary<Guid, string>
        {
            [first] = Raw(first, "paymentin", baseId),
            [second] = Raw(second, "supply", baseId)
        });
        var builder = new FactureInPayloadBuilder(raw, new ItemRepository(), new BaseGateway(new HashSet<FactureInBaseReference>()));

        var result = await builder.BuildAsync(Guid.NewGuid(), Guid.NewGuid(), [first, second], CancellationToken.None);

        Assert.Empty(result.PreparedDocuments);
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == first && item.ErrorCode == "FACTUREIN_UNSUPPORTED_BASE_TYPE");
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == second && item.ErrorCode == "FACTUREIN_BASE_DOCUMENT_MISSING");
    }

    [Fact]
    public async Task BuildAsync_AcceptsCashOutInPaymentsAsABase()
    {
        var documentId = Guid.NewGuid();
        var cashOutId = Guid.NewGuid();
        var builder = new FactureInPayloadBuilder(
            new RawDataRepository(documentId, Raw(documentId, "cashout", cashOutId)),
            new ItemRepository(),
            new BaseGateway(new HashSet<FactureInBaseReference> { new("cashout", cashOutId) }));

        var result = await builder.BuildAsync(Guid.NewGuid(), Guid.NewGuid(), [documentId], CancellationToken.None);

        Assert.Single(result.PreparedDocuments);
        Assert.Empty(result.SkippedDocuments);
    }

    private static string Raw(Guid documentId, string type, Guid baseId)
    {
        var field = type == "supply" ? "supplies" : "payments";
        return $$$"""
            {"id":"{{{documentId:D}}}","agent":{"meta":{"href":"https://api.test/entity/counterparty/{{{Guid.NewGuid():D}}}","type":"counterparty"}},"organization":{"meta":{"href":"https://api.test/entity/organization/{{{Guid.NewGuid():D}}}","type":"organization"}},"{{{field}}}":[{"meta":{"href":"https://api.test/entity/{{{type}}}/{{{baseId:D}}}","type":"{{{type}}}"}}]}
            """;
    }

    private sealed class RawDataRepository : IFactureInRawDataRepository
    {
        private readonly IReadOnlyDictionary<Guid, string> _documents;
        public RawDataRepository(Guid id, string raw) : this(new Dictionary<Guid, string> { [id] = raw }) { }
        public RawDataRepository(IReadOnlyDictionary<Guid, string> documents) => _documents = documents;
        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(documentIds.ToDictionary(id => id, id => _documents[id]));
        public Task UpsertAsync(Guid accountId, IReadOnlyDictionary<Guid, string> documents, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ItemRepository : IFactureInRecreationItemRepository
    {
        public List<FactureInRecreationItem> Created { get; } = [];
        public Task<IReadOnlyDictionary<Guid, FactureInRecreationItem>> GetAsync(Guid accountId, IReadOnlyCollection<Guid> sourceFactureInIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FactureInRecreationItem>>(Created.Where(item => sourceFactureInIds.Contains(item.SourceFactureInId)).ToDictionary(item => item.SourceFactureInId));
        public Task<FactureInRecreationItem> CreateOrGetAsync(FactureInRecreationItem item, CancellationToken cancellationToken)
        {
            var current = Created.SingleOrDefault(existing => existing.SourceFactureInId == item.SourceFactureInId);
            if (current is not null) return Task.FromResult(current);
            Created.Add(item); return Task.FromResult(item);
        }
        public Task SaveAsync(FactureInRecreationItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BaseGateway(IReadOnlySet<FactureInBaseReference> existing) : IMoySkladFactureInGateway
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId, string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlySet<FactureInBaseReference>> GetExistingBasesAsync(Guid accountId, string correlationId, IReadOnlyCollection<FactureInBaseReference> references, CancellationToken cancellationToken) => Task.FromResult(existing);
    }
}

public sealed class FactureInDocumentTransferServiceTests
{
    [Fact]
    public async Task TransferAsync_DoesNotCreateForDeleteFailureAndMapsSuccessBySyncId()
    {
        var successful = Item("Prepared");
        var deleteFailed = Item("Prepared");
        var gateway = new TransferGateway(deleteFailed.SourceFactureInId, successful.NewSyncId);
        var service = new FactureInDocumentTransferService(gateway, new SavingRepository(), new FactureInRecreationOptions(1, TimeSpan.Zero));

        var result = await service.TransferAsync(successful.AccountId, [successful, deleteFailed], CancellationToken.None);

        Assert.Equal([successful.SourceFactureInId], result.TransferredDocumentIds);
        Assert.Equal("Completed", successful.Stage);
        Assert.Equal("DeleteFailed", deleteFailed.Stage);
        Assert.Single(gateway.CreateRequests);
        Assert.Equal([successful.SourceFactureInId], gateway.CreateRequests.Single().Select(item => item.SourceDocumentId));
        Assert.Contains(result.FailedDocuments, item => item.SourceDocumentId == deleteFailed.SourceFactureInId);
    }

    [Fact]
    public async Task TransferAsync_MarksMissingSyncIdAsResponseMappingFailure()
    {
        var item = Item("Deleted");
        var gateway = new TransferGateway(null, null);
        var service = new FactureInDocumentTransferService(gateway, new SavingRepository(), new FactureInRecreationOptions(1, TimeSpan.Zero));

        var result = await service.TransferAsync(item.AccountId, [item], CancellationToken.None);

        var failed = Assert.Single(result.FailedDocuments);
        Assert.Equal("FACTUREIN_RESPONSE_MAPPING_FAILED", failed.ErrorCode);
        Assert.Equal("ResponseMappingFailed", item.Stage);
    }

    private static FactureInRecreationItem Item(string stage) => new()
    {
        AccountId = Guid.NewGuid(), SourceFactureInId = Guid.NewGuid(), MainCounterpartyId = Guid.NewGuid(),
        NewSyncId = Guid.NewGuid(), PayloadJson = "{}", Stage = stage, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class SavingRepository : IFactureInRecreationItemRepository
    {
        public Task<IReadOnlyDictionary<Guid, FactureInRecreationItem>> GetAsync(Guid accountId, IReadOnlyCollection<Guid> sourceFactureInIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FactureInRecreationItem> CreateOrGetAsync(FactureInRecreationItem item, CancellationToken cancellationToken) => Task.FromResult(item);
        public Task SaveAsync(FactureInRecreationItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TransferGateway(Guid? deleteFailure, Guid? successfulSyncId) : IMoySkladFactureInGateway
    {
        public List<IReadOnlyList<MoySkladFactureInBatchCreateItem>> CreateRequests { get; } = [];
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId, string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MoySkladFactureInBatchDeleteResult>> DeleteBatchAsync(Guid accountId, string correlationId, IReadOnlyList<Guid> factureInIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladFactureInBatchDeleteResult>>(factureInIds.Select(id => new MoySkladFactureInBatchDeleteResult(id, id != deleteFailure, "DELETE_FAILED", "delete failed")).ToArray());
        public Task<IReadOnlyList<MoySkladFactureInBatchCreateResult>> CreateBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladFactureInBatchCreateItem> documents, CancellationToken cancellationToken)
        {
            CreateRequests.Add(documents.ToArray());
            return Task.FromResult<IReadOnlyList<MoySkladFactureInBatchCreateResult>>(
                successfulSyncId is null ? [new MoySkladFactureInBatchCreateResult(0, Guid.NewGuid(), null, "facturein")] :
                [new MoySkladFactureInBatchCreateResult(0, Guid.NewGuid(), successfulSyncId, "facturein")]);
        }
    }
}
