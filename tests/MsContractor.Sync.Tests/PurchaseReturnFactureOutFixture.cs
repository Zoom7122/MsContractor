using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

internal sealed class PurchaseReturnFactureOutFixture
{
    public PurchaseReturnFactureOutFixture(List<string>? events = null)
    {
        Events = events ?? [];
        Repository = new MemoryRepository();
        Gateway = new RecordingGateway(Events);
        Builder = new RecordingBuilder(Events, new FactureOutPayloadBuilder(Repository, Repository));
        Service = new PurchaseReturnFactureOutService(new Preparation(Events, Repository), Builder, Repository,
            new FactureOutDocumentRecreationService(Gateway, new FactureOutDocumentRecreationVerifier(), Repository), Gateway);
    }

    public List<string> Events { get; }
    public MemoryRepository Repository { get; }
    public RecordingGateway Gateway { get; }
    public RecordingBuilder Builder { get; }
    public PurchaseReturnFactureOutService Service { get; }

    public Guid AddInvoice(params Guid[] returnIds)
    {
        var id = Guid.NewGuid();
        Repository.Raw[id] = new JsonObject
        {
            ["id"] = id.ToString(), ["name"] = "invoice",
            ["agent"] = Reference("counterparty", Guid.NewGuid()),
            ["returns"] = new JsonArray(returnIds.Select(value => (JsonNode)Reference("purchasereturn", value)).ToArray()),
            ["demands"] = new JsonArray(Reference("demand", Guid.NewGuid()))
        }.ToJsonString();
        return id;
    }

    private static JsonObject Reference(string type, Guid id) => new()
    {
        ["meta"] = new JsonObject
        {
            ["type"] = type, ["href"] = $"https://api.example.test/api/remap/1.2/entity/{type}/{id:D}"
        }
    };

    private sealed class Preparation : IFactureOutPreparationService
    {
        private readonly List<string> _events;
        private readonly MemoryRepository _repository;
        public Preparation(List<string> events, MemoryRepository repository) { _events = events; _repository = repository; }
        public Task<FactureOutPreparationResult> PrepareAsync(Guid accountId, IReadOnlyList<Guid> ids, CancellationToken token)
        {
            _events.Add("invoice-prepare");
            return Task.FromResult(new FactureOutPreparationResult(ids.Where(_repository.Raw.ContainsKey).ToArray(),
                ids.Where(id => !_repository.Raw.ContainsKey(id)).Select(id =>
                    new FactureOutSkippedDocumentResult(id, "Skipped", "NOT_FOUND", "Missing invoice.")).ToArray()));
        }
    }

    internal sealed class RecordingBuilder : IFactureOutPayloadBuilder
    {
        private readonly List<string> _events;
        private readonly IFactureOutPayloadBuilder _inner;
        public RecordingBuilder(List<string> events, IFactureOutPayloadBuilder inner) { _events = events; _inner = inner; }
        public List<FactureOutPayload> Built { get; } = [];
        public async Task<FactureOutPayloadBuildResult> BuildAsync(Guid accountId, Guid mainCounterpartyId,
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
        {
            _events.Add("invoice-payload");
            var result = await _inner.BuildAsync(accountId, mainCounterpartyId, ids, cancellationToken);
            Built.AddRange(result.Payloads);
            return result;
        }
    }

    internal sealed class MemoryRepository : IFactureOutRawDataRepository, IFactureOutRecreationItemRepository
    {
        public Dictionary<Guid, string> Raw { get; } = [];
        public Dictionary<Guid, Guid> Created { get; } = [];
        public HashSet<Guid> Failed { get; } = [];
        public Exception? SaveException { get; set; }
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(Raw.Where(pair => ids.Contains(pair.Key)).ToDictionary());
        public Task UpsertAsync(Guid accountId, IReadOnlyDictionary<Guid, string> documents, CancellationToken token) => throw new NotSupportedException();
        public Task UpsertPreparationAsync(Guid accountId, IReadOnlyDictionary<Guid, Guid?> syncIds, IReadOnlyCollection<Guid> skipped, CancellationToken token) => Task.CompletedTask;
        public Task SavePayloadBuildResultsAsync(Guid accountId, IReadOnlyDictionary<Guid, Guid> syncIds, IReadOnlyCollection<Guid> skipped, CancellationToken token) => Task.CompletedTask;
        public Task SaveRecreationResultsAsync(Guid accountId, IReadOnlyDictionary<Guid, Guid> transferred,
            IReadOnlyCollection<Guid> failed, CancellationToken cancellationToken)
        {
            if (SaveException is not null) throw SaveException;
            foreach (var pair in transferred) Created[pair.Key] = pair.Value;
            Failed.UnionWith(failed);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingGateway : IMoySkladFactureOutGateway
    {
        private readonly List<string> _events;
        private readonly Dictionary<Guid, string> _createdRaw = [];
        public RecordingGateway(List<string> events) => _events = events;
        public HashSet<Guid> RejectedDeleteIds { get; } = [];
        public HashSet<Guid> RejectedCreateIds { get; } = [];
        public List<Guid> DeletedIds { get; } = [];
        public List<MoySkladFactureOutBatchCreateItem> CreatedItems { get; } = [];
        public List<IReadOnlyList<MoySkladFactureOutBatchCreateItem>> CreatedBatches { get; } = [];
        public bool InvalidVerifiedBasis { get; set; }
        public Exception? DeleteException { get; set; }
        public int? FailingCreateAttempt { get; set; }
        public Guid? AccountId { get; private set; }
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, string correlationId,
            IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
        {
            _events.Add("invoice-verify");
            AccountId = accountId;
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(ids.Where(_createdRaw.ContainsKey)
                .ToDictionary(id => id, id => _createdRaw[id]));
        }
        public Task<IReadOnlyList<MoySkladFactureOutBatchDeleteResult>> DeleteBatchAsync(Guid accountId,
            string correlationId, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
        {
            _events.Add("invoice-delete");
            AccountId = accountId;
            DeletedIds.AddRange(ids);
            if (DeleteException is not null) throw DeleteException;
            return Task.FromResult<IReadOnlyList<MoySkladFactureOutBatchDeleteResult>>(ids.Select(id =>
                new MoySkladFactureOutBatchDeleteResult(id, !RejectedDeleteIds.Contains(id),
                    RejectedDeleteIds.Contains(id) ? "DELETE_REJECTED" : null)).ToArray());
        }
        public Task<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateBatchAsync(Guid accountId,
            string correlationId, IReadOnlyList<MoySkladFactureOutBatchCreateItem> items, CancellationToken cancellationToken)
        {
            _events.Add("invoice-create");
            AccountId = accountId;
            CreatedBatches.Add(items);
            CreatedItems.AddRange(items);
            if (FailingCreateAttempt == CreatedBatches.Count)
                throw new InvalidOperationException("Unknown invoice create result.");
            return Task.FromResult<IReadOnlyList<MoySkladFactureOutBatchCreateResult>>(items.Select((item, index) =>
            {
                if (RejectedCreateIds.Contains(item.SourceDocumentId))
                    return new MoySkladFactureOutBatchCreateResult(index, null, item.SyncId, "factureout", "CREATE_REJECTED", "Invoice rejected.");
                var id = Guid.NewGuid();
                var json = JsonNode.Parse(item.PayloadJson)!.AsObject();
                if (InvalidVerifiedBasis) json["returns"] = new JsonArray(Reference("purchasereturn", Guid.NewGuid()));
                _createdRaw[id] = json.ToJsonString();
                return new MoySkladFactureOutBatchCreateResult(index, id, item.SyncId, "factureout");
            }).ToArray());
        }
    }
}
