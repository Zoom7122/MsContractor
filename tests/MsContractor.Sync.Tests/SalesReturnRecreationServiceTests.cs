using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.Sync.Tests;

public sealed class SalesReturnRecreationServiceTests
{
    [Fact]
    public async Task RecreateAsync_DeletesAllBatchesBeforeCreatingNewBatches()
    {
        var accountId = Guid.NewGuid();
        var mainAgentId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var documents = ids.ToDictionary(id => id, SourceDocument);
        var positions = ids.ToDictionary(id => id, id => Positions(id));
        var gateway = new RecordingGateway([new MoySkladSalesReturnAgentAccount(Guid.NewGuid(), true)]);
        var operations = new RecordingOperationsRepository();

        var result = await Service(documents, positions, operations, gateway).RecreateAsync(
            accountId, mainAgentId, ids, CancellationToken.None);

        Assert.Equal(["delete:1000", "delete:1", "create:1000", "create:1"], gateway.Writes);
        Assert.All(result.Documents, document => Assert.Equal("Completed", document.Status));
        Assert.Equal("Completed", operations.Operation!.Status);
        Assert.All(operations.Operation.Items, item => Assert.NotNull(item.TargetAgentAccountId));
    }

    [Theory]
    [InlineData("default", true)]
    [InlineData("single", true)]
    [InlineData("multiple", false)]
    public async Task RecreateAsync_SelectsConfiguredAgentAccountFallback(string scenario, bool expectedAgentAccount)
    {
        var sourceId = Guid.NewGuid();
        IReadOnlyList<MoySkladSalesReturnAgentAccount> accounts = scenario switch
        {
            "default" => [new(Guid.NewGuid(), false), new(Guid.NewGuid(), true)],
            "single" => [new(Guid.NewGuid(), false)],
            _ => [new(Guid.NewGuid(), false), new(Guid.NewGuid(), false)]
        };
        var gateway = new RecordingGateway(accounts);
        var result = await Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            new RecordingOperationsRepository(),
            gateway).RecreateAsync(Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None);

        Assert.Equal("Completed", Assert.Single(result.Documents).Status);
        var payload = JsonNode.Parse(Assert.Single(gateway.NewPayloads))!.AsObject();
        Assert.Equal(expectedAgentAccount, payload["agentAccount"] is not null);
        Assert.Null(payload["contract"]);
    }

    [Fact]
    public async Task RecreateAsync_AcceptsCreatedDocumentWithoutValidatingMoySkladBody()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway([]) { ReturnInvalidNewDocument = true };
        var operations = new RecordingOperationsRepository();
        var result = await Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            operations,
            gateway).RecreateAsync(Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("Completed", document.Status);
        Assert.Equal("Completed", document.Stage);
        Assert.Null(document.ErrorCode);
        Assert.Equal(["delete:1", "create:1"], gateway.Writes);
        Assert.NotNull(document.NewDocumentId);
        Assert.Equal("Completed", operations.Operation!.Status);
    }

    [Fact]
    public async Task RecreateAsync_CreateFailureWithoutDocumentDoesNotRestoreSource()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway([]) { ReturnCreateErrorWithoutDocument = true };
        var operations = new RecordingOperationsRepository();

        var result = await Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            operations,
            gateway).RecreateAsync(Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("Failed", document.Status);
        Assert.Equal("SALESRETURN_NEW_CREATE_FAILED", document.ErrorCode);
        Assert.Equal(["delete:1", "create:1"], gateway.Writes);
        Assert.Equal("Failed", operations.Operation!.Status);
    }

    [Fact]
    public async Task RecreateAsync_DoesNotCallGatewayWhenSavedPositionsAreMissing()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>(),
            new RecordingOperationsRepository(),
            gateway).RecreateAsync(Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None));

        Assert.Empty(gateway.Writes);
    }

    [Fact]
    public async Task RecreateAsync_LoadsDocumentsThenPositionsBeforeMutatingMoySklad()
    {
        var sourceId = Guid.NewGuid();
        var events = new List<string>();
        var gateway = new RecordingGateway([], events);

        await Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            new RecordingOperationsRepository(),
            gateway,
            new RecordingDocumentLoader(events),
            new RecordingPositionsLoader(events)).RecreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None);

        Assert.Equal(["load-documents", "load-positions", "delete:1", "create:1"], events);
    }

    [Fact]
    public async Task RecreateAsync_DoesNotMutateMoySkladWhenDocumentLoadingFails()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway([]);
        var positionsLoader = new RecordingPositionsLoader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            new RecordingOperationsRepository(),
            gateway,
            new RecordingDocumentLoader(exception: new InvalidOperationException("documents unavailable")),
            positionsLoader).RecreateAsync(Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None));

        Assert.False(positionsLoader.WasCalled);
        Assert.Empty(gateway.Writes);
    }

    [Fact]
    public async Task RecreateAsync_DoesNotMutateMoySkladWhenPositionLoadingFails()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway([]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(
            new Dictionary<Guid, string> { [sourceId] = SourceDocument(sourceId) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = Positions(sourceId) },
            new RecordingOperationsRepository(),
            gateway,
            new RecordingDocumentLoader(),
            new RecordingPositionsLoader(exception: new InvalidOperationException("positions unavailable"))).RecreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), [sourceId], CancellationToken.None));

        Assert.Empty(gateway.Writes);
    }

    private static SalesReturnRecreationService Service(
        IReadOnlyDictionary<Guid, string> documents,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> positions,
        RecordingOperationsRepository operations,
        RecordingGateway gateway,
        IMoySkladSalesReturnServiceGetData? documentLoader = null,
        IMoySkladSalesReturnPositionsService? positionsLoader = null) => new(
        new RawDataRepository(documents),
        new PositionsRepository(positions),
        documentLoader ?? new RecordingDocumentLoader(),
        positionsLoader ?? new RecordingPositionsLoader(),
        operations,
        gateway,
        new SalesReturnRecreationPayloadBuilder(),
        TimeProvider.System);

    private static string SourceDocument(Guid documentId)
    {
        var organizationId = Guid.NewGuid();
        var storeId = Guid.NewGuid();
        var demandId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var agentAccountId = Guid.NewGuid();
        return JsonSerializer.Serialize(new
        {
            id = documentId,
            accountId = Guid.NewGuid(),
            created = "2026-09-16 12:00:00",
            agent = Reference("counterparty", agentId),
            agentAccount = Reference("account", agentAccountId, $"entity/counterparty/{agentId:D}/accounts/{agentAccountId:D}"),
            organization = Reference("organization", organizationId),
            store = Reference("store", storeId),
            demand = Reference("demand", demandId),
            contract = Reference("contract", contractId),
            positions = new { rows = Array.Empty<object>() }
        });
    }

    private static IReadOnlyDictionary<Guid, string> Positions(Guid sourceId)
    {
        var positionId = Guid.NewGuid();
        var assortmentId = Guid.NewGuid();
        return new Dictionary<Guid, string>
        {
            [positionId] = JsonSerializer.Serialize(new
            {
                id = positionId,
                accountId = Guid.NewGuid(),
                assortment = Reference("product", assortmentId),
                quantity = 2,
                price = 100,
                vat = 20
            })
        };
    }

    private static object Reference(string type, Guid id, string? path = null) => new
    {
        meta = new
        {
            href = "https://api.moysklad.ru/api/remap/1.2/" + (path ?? $"entity/{type}/{id:D}"),
            type,
            mediaType = "application/json"
        }
    };

    private sealed class RawDataRepository : ISalesReturnRawDataRepository
    {
        private readonly IReadOnlyDictionary<Guid, string> _documents;

        public RawDataRepository(IReadOnlyDictionary<Guid, string> documents)
        {
            _documents = documents;
        }

        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
            Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
        {
            if (documentIds.Any(id => !_documents.ContainsKey(id)))
                throw new InvalidOperationException("Raw data is missing.");
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(documentIds.ToDictionary(id => id, id => _documents[id]));
        }

        public Task UpsertAsync(Guid accountId, IReadOnlyDictionary<Guid, string> documents, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class PositionsRepository : ISalesReturnPositionRawDataRepository
    {
        private readonly IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> _positions;

        public PositionsRepository(IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> positions)
        {
            _positions = positions;
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredAsync(
            Guid accountId, IReadOnlyCollection<Guid> salesReturnIds, CancellationToken cancellationToken)
        {
            if (salesReturnIds.Any(id => !_positions.ContainsKey(id)))
                throw new InvalidOperationException("Positions are missing.");
            return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>>(
                salesReturnIds.ToDictionary(id => id, id => _positions[id]));
        }

        public Task ReplaceAsync(Guid accountId, Guid salesReturnId, IReadOnlyDictionary<Guid, string> positions,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDocumentLoader : IMoySkladSalesReturnServiceGetData
    {
        private readonly List<string>? _events;
        private readonly Exception? _exception;

        public RecordingDocumentLoader(List<string>? events = null, Exception? exception = null)
        {
            _events = events;
            _exception = exception;
        }

        public Task LoadAsync(Guid accountId, Guid requestedByUserId, string correlationId,
            IReadOnlyList<Guid> salesReturnIds, CancellationToken cancellationToken)
        {
            _events?.Add("load-documents");
            return _exception is null ? Task.CompletedTask : Task.FromException(_exception);
        }
    }

    private sealed class RecordingPositionsLoader : IMoySkladSalesReturnPositionsService
    {
        private readonly List<string>? _events;
        private readonly Exception? _exception;

        public RecordingPositionsLoader(List<string>? events = null, Exception? exception = null)
        {
            _events = events;
            _exception = exception;
        }

        public bool WasCalled { get; private set; }

        public Task LoadAsync(Guid accountId, Guid requestedByUserId, string correlationId,
            IReadOnlyList<Guid> salesReturnIds, CancellationToken cancellationToken)
        {
            WasCalled = true;
            _events?.Add("load-positions");
            return _exception is null ? Task.CompletedTask : Task.FromException(_exception);
        }
    }

    private sealed class RecordingOperationsRepository : ISalesReturnRecreationOperationRepository
    {
        public SalesReturnRecreationOperation? Operation { get; private set; }

        public Task CreateAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken)
        {
            Operation = operation;
            return Task.CompletedTask;
        }

        public Task SaveAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken)
        {
            Operation = operation;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGateway : IMoySkladSalesReturnGateway
    {
        private readonly IReadOnlyList<MoySkladSalesReturnAgentAccount> _accounts;
        private readonly List<string>? _events;

        public RecordingGateway(IReadOnlyList<MoySkladSalesReturnAgentAccount> accounts, List<string>? events = null)
        {
            _accounts = accounts;
            _events = events;
        }

        public List<string> Writes { get; } = [];
        public List<string> NewPayloads { get; } = [];
        public bool ReturnInvalidNewDocument { get; set; }
        public bool ReturnCreateErrorWithoutDocument { get; set; }
        private int _createCalls;

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId,
            string correlationId, IReadOnlyList<Guid> salesReturnIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<IReadOnlyList<MoySkladSalesReturnAgentAccount>> GetAgentAccountsAsync(Guid accountId,
            Guid mainAgentId, string correlationId, CancellationToken cancellationToken) => Task.FromResult(_accounts);

        public Task<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>> DeleteBatchAsync(Guid accountId,
            string correlationId, IReadOnlyList<Guid> salesReturnIds, CancellationToken cancellationToken)
        {
            Writes.Add($"delete:{salesReturnIds.Count}");
            _events?.Add($"delete:{salesReturnIds.Count}");
            return Task.FromResult<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>>(
                salesReturnIds.Select(id => new MoySkladSalesReturnBatchDeleteResult(id, true)).ToArray());
        }

        public Task<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>> CreateBatchAsync(Guid accountId,
            string correlationId, IReadOnlyList<MoySkladSalesReturnBatchCreateItem> documents,
            CancellationToken cancellationToken)
        {
            _createCalls++;
            Writes.Add($"create:{documents.Count}");
            _events?.Add($"create:{documents.Count}");
            if (_createCalls == 1)
                NewPayloads.AddRange(documents.Select(item => item.PayloadJson));
            return Task.FromResult<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>>(documents.Select(item =>
            {
                if (ReturnCreateErrorWithoutDocument && _createCalls == 1)
                {
                    return new MoySkladSalesReturnBatchCreateResult(
                        item.SourceDocumentId, item.SyncId, null, null,
                        "CREATE_FAILED", "create failed");
                }
                var response = item.PayloadJson;
                if (ReturnInvalidNewDocument && _createCalls == 1)
                {
                    var json = JsonNode.Parse(response)!.AsObject();
                    json["organization"] = JsonSerializer.SerializeToNode(Reference("organization", Guid.NewGuid()));
                    response = json.ToJsonString();
                }
                return new MoySkladSalesReturnBatchCreateResult(
                    item.SourceDocumentId, item.SyncId, Guid.NewGuid(), response);
            }).ToArray());
        }
    }
}
