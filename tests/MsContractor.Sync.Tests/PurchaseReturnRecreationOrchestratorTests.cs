using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnRecreationOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_PreparesAllPayloadsBeforeDelete_AndCreatesOnlyDeletedDocuments()
    {
        var firstId = Guid.NewGuid();
        var invalidId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var events = new List<string>();
        var gateway = new RecordingGateway(events)
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(
                id, id == firstId)).ToArray(),
            CreateResults = items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                item.SourceDocumentId, Guid.NewGuid(), item.PayloadJson)).ToArray()
        };
        var repository = new RecordingRepository(
            new Dictionary<Guid, string>
            {
                [firstId] = DocumentJson(),
                [invalidId] = "not-json"
            },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>
            {
                [firstId] = new Dictionary<Guid, string> { [Guid.NewGuid()] = PositionJson() },
                [invalidId] = new Dictionary<Guid, string> { [Guid.NewGuid()] = PositionJson() }
            });
        var verifier = new RecordingVerifier(events);
        var orchestrator = CreateOrchestrator(
            new PreparationStub([firstId, invalidId]), repository, gateway,
            verifier: verifier);

        await orchestrator.ExecuteAsync(
            Guid.NewGuid(), mainCounterpartyId, [firstId, invalidId], CancellationToken.None);

        Assert.Equal(["delete", "create", "verify"], events);
        Assert.Single(gateway.DeletedBatches);
        Assert.Equal([firstId], gateway.DeletedBatches[0]);
        Assert.Single(gateway.CreatedBatches);
        Assert.Equal([firstId], gateway.CreatedBatches[0].Select(item => item.SourceDocumentId));
        Assert.Equal([firstId], verifier.VerifiedIds);
    }

    [Fact]
    public async Task ExecuteAsync_ReattachesAfterCreateAndBeforeVerification()
    {
        var sourceId = Guid.NewGuid();
        var events = new List<string>();
        var gateway = new RecordingGateway(events)
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray(),
            CreateResults = items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                item.SourceDocumentId, Guid.NewGuid(), item.PayloadJson)).ToArray()
        };
        var verifier = new RecordingVerifier(events);
        var orchestrator = CreateOrchestrator(
            new PreparationStub([sourceId]),
            RepositoryFor(sourceId),
            gateway,
            verifier: verifier,
            factureRelations: new RecordingFactureRelationsService(events));

        await orchestrator.ExecuteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        Assert.Equal(["delete", "create", "reattach", "verify"], events);
        Assert.Equal([sourceId], verifier.VerifiedIds);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCreatedWithErrorAndSkipsVerificationWhenReattachFails()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var events = new List<string>();
        var gateway = new RecordingGateway(events)
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray(),
            CreateResults = items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                item.SourceDocumentId, newId, item.PayloadJson)).ToArray()
        };
        var verifier = new RecordingVerifier(events);
        var orchestrator = CreateOrchestrator(
            new PreparationStub([sourceId]),
            RepositoryFor(sourceId),
            gateway,
            verifier: verifier,
            factureRelations: new FailingReattachFactureRelationsService());

        var result = await orchestrator.ExecuteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal(newId, document.NewDocumentId);
        Assert.Equal("CreatedWithError", document.Status);
        Assert.Equal("PURCHASERETURN_PAYMENT_RELATIONS_REATTACH_FAILED", document.ErrorCode);
        Assert.Empty(verifier.VerifiedIds);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotLoadOrCreatePayloadForFactureRelationFailures()
    {
        var readyId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        var gateway = new RecordingGateway([])
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray(),
            CreateResults = items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                item.SourceDocumentId,
                Guid.NewGuid(),
                item.PayloadJson)).ToArray()
        };
        var orchestrator = CreateOrchestrator(
            new PreparationStub([readyId, failedId]),
            RepositoryFor(readyId, failedId),
            gateway,
            factureRelations: new FilteringFactureRelationsService(readyId, failedId));

        var result = await orchestrator.ExecuteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [readyId, failedId],
            CancellationToken.None);

        Assert.Single(gateway.DeletedBatches);
        Assert.Equal([readyId], gateway.DeletedBatches[0]);
        Assert.Single(gateway.CreatedBatches);
        Assert.Equal([readyId], gateway.CreatedBatches[0].Select(item => item.SourceDocumentId));
        var failed = result.Documents.Single(item => item.SourceDocumentId == failedId);
        Assert.Equal("Failed", failed.Status);
        Assert.Equal("FACTURE_RELATION_FAILED", failed.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesItemLevelCreateErrorsWithoutRetryingSuccessfulItems()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var createAttempt = 0;
        var gateway = new RecordingGateway([])
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray(),
            CreateResults = items =>
            {
                createAttempt++;
                return items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                    item.SourceDocumentId,
                    createAttempt == 1 && item.SourceDocumentId == secondId ? null : Guid.NewGuid(),
                    null,
                    createAttempt == 1 && item.SourceDocumentId == secondId ? "ITEM_ERROR" : null,
                    createAttempt == 1 && item.SourceDocumentId == secondId ? "failed" : null)).ToArray();
            }
        };
        var repository = RepositoryFor(firstId, secondId);
        var orchestrator = CreateOrchestrator(
            new PreparationStub([firstId, secondId]),
            repository,
            gateway,
            new PurchaseReturnRecreationOptions(2, TimeSpan.Zero),
            new NoopVerifier());

        await orchestrator.ExecuteAsync(
            Guid.NewGuid(), Guid.NewGuid(), [firstId, secondId], CancellationToken.None);

        Assert.Equal(2, gateway.CreatedBatches.Count);
        Assert.Equal([firstId, secondId], gateway.CreatedBatches[0].Select(item => item.SourceDocumentId));
        Assert.Equal([secondId], gateway.CreatedBatches[1].Select(item => item.SourceDocumentId));
    }

    [Fact]
    public async Task ExecuteAsync_RetriesFailedDeleteItems()
    {
        var documentId = Guid.NewGuid();
        var deleteAttempt = 0;
        var gateway = new RecordingGateway([])
        {
            DeleteResults = ids =>
            {
                deleteAttempt++;
                return ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(
                    id, deleteAttempt > 1)).ToArray();
            },
            CreateResults = items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(
                item.SourceDocumentId, Guid.NewGuid(), item.PayloadJson)).ToArray()
        };
        var orchestrator = CreateOrchestrator(
            new PreparationStub([documentId]),
            RepositoryFor(documentId),
            gateway,
            new PurchaseReturnRecreationOptions(2, TimeSpan.Zero),
            new NoopVerifier());

        await orchestrator.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), [documentId], CancellationToken.None);

        Assert.Equal(2, gateway.DeletedBatches.Count);
        Assert.Single(gateway.CreatedBatches);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryPostAfterTransportFailure()
    {
        var documentId = Guid.NewGuid();
        var gateway = new RecordingGateway([])
        {
            DeleteResults = ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray(),
            CreateResults = _ => throw new EgressException(503, "TRANSPORT", "unknown result")
        };
        var orchestrator = CreateOrchestrator(
            new PreparationStub([documentId]),
            RepositoryFor(documentId),
            gateway,
            new PurchaseReturnRecreationOptions(3, TimeSpan.Zero),
            new NoopVerifier());

        await orchestrator.ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), [documentId], CancellationToken.None);

        Assert.Single(gateway.CreatedBatches);
    }

    private static PurchaseReturnRecreationOrchestrator CreateOrchestrator(
        IPurchaseReturnPreparationService preparation,
        IPurchaseReturnPreparationRepository repository,
        RecordingGateway gateway,
        PurchaseReturnRecreationOptions? options = null,
        IPurchaseReturnVerifier? verifier = null,
        IPurchaseReturnFactureRelationsService? factureRelations = null) =>
        new(
            preparation,
            factureRelations ?? new NoopFactureRelationsService(),
            repository,
            gateway,
            verifier ?? new NoopVerifier(),
            new PurchaseReturnCreateMapper(Options.Create(new EgressOptions
            {
                JsonApiBaseUrl = new Uri("https://api.example.test/api/remap/1.2/")
            })),
            options ?? new PurchaseReturnRecreationOptions(1, TimeSpan.Zero),
            NullLogger<PurchaseReturnRecreationOrchestrator>.Instance);

    private sealed class NoopFactureRelationsService : IPurchaseReturnFactureRelationsService
    {
        public Task<PurchaseReturnFactureRelationsResult> CheckAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnFactureRelationsResult(
                purchaseReturnIds
                    .Select(id => new PurchaseReturnFactureRelationResult(id, "NoRelations"))
                    .ToArray()));

        public Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
            IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnRelationsReattachResult(
                oldToNewPurchaseReturnIds.Select(item => new PurchaseReturnRelationsReattachItem(
                    item.Key,
                    item.Value,
                    "NoRelations")).ToArray()));
    }

    private sealed class FilteringFactureRelationsService(Guid readyId, Guid failedId)
        : IPurchaseReturnFactureRelationsService
    {
        public Task<PurchaseReturnFactureRelationsResult> CheckAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnFactureRelationsResult(
                purchaseReturnIds.Select(id => id == readyId
                    ? new PurchaseReturnFactureRelationResult(id, "Detached")
                    : new PurchaseReturnFactureRelationResult(
                        failedId,
                        "Failed",
                        "FACTURE_RELATION_FAILED",
                        "facture relation detach failed.")).ToArray()));

        public Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
            IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnRelationsReattachResult(
                oldToNewPurchaseReturnIds.Select(item => new PurchaseReturnRelationsReattachItem(
                    item.Key,
                    item.Value,
                    "NoRelations")).ToArray()));
    }

    private sealed class RecordingFactureRelationsService(List<string> events)
        : IPurchaseReturnFactureRelationsService
    {
        public Task<PurchaseReturnFactureRelationsResult> CheckAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnFactureRelationsResult(
                purchaseReturnIds
                    .Select(id => new PurchaseReturnFactureRelationResult(id, "NoRelations"))
                    .ToArray()));

        public Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
            IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
            CancellationToken cancellationToken)
        {
            events.Add("reattach");
            return Task.FromResult(new PurchaseReturnRelationsReattachResult(
                oldToNewPurchaseReturnIds.Select(item => new PurchaseReturnRelationsReattachItem(
                    item.Key,
                    item.Value,
                    "NoRelations")).ToArray()));
        }
    }

    private sealed class FailingReattachFactureRelationsService
        : IPurchaseReturnFactureRelationsService
    {
        public Task<PurchaseReturnFactureRelationsResult> CheckAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnFactureRelationsResult(
                purchaseReturnIds
                    .Select(id => new PurchaseReturnFactureRelationResult(id, "NoRelations"))
                    .ToArray()));

        public Task<PurchaseReturnRelationsReattachResult> ReattachAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> detachedPurchaseReturnIds,
            IReadOnlyDictionary<Guid, Guid> oldToNewPurchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnRelationsReattachResult(
                oldToNewPurchaseReturnIds.Select(item => new PurchaseReturnRelationsReattachItem(
                    item.Key,
                    item.Value,
                    "Failed",
                    "PURCHASERETURN_PAYMENT_RELATIONS_REATTACH_FAILED",
                    "Could not reattach cashin 00000000-0000-0000-0000-000000000001 for purchasereturn."))
                    .ToArray()));
    }

    private sealed class NoopVerifier : IPurchaseReturnVerifier
    {
        public Task<PurchaseReturnVerificationResult> VerifyAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<PurchaseReturnVerificationInput> documents,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnVerificationResult(
                documents.Select(item => new PurchaseReturnDocumentVerificationResult(
                    item.SourceDocumentId, item.NewDocumentId, "Verified", [], [], [])).ToArray()));
    }

    private sealed class RecordingVerifier(List<string> events) : IPurchaseReturnVerifier
    {
        public List<Guid> VerifiedIds { get; } = [];

        public Task<PurchaseReturnVerificationResult> VerifyAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<PurchaseReturnVerificationInput> documents,
            CancellationToken cancellationToken)
        {
            events.Add("verify");
            VerifiedIds.AddRange(documents.Select(item => item.SourceDocumentId));
            return Task.FromResult(new PurchaseReturnVerificationResult(
                documents.Select(item => new PurchaseReturnDocumentVerificationResult(
                    item.SourceDocumentId, item.NewDocumentId, "Verified", [], [], [])).ToArray()));
        }
    }

    private static RecordingRepository RepositoryFor(params Guid[] ids) =>
        new(
            ids.ToDictionary(id => id, _ => DocumentJson()),
            ids.ToDictionary(
                id => id,
                id => (IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>
                {
                    [Guid.NewGuid()] = PositionJson()
                }));

    private static string DocumentJson() =>
        """{"organization":{"meta":{"href":"https://old/entity/organization/00000000-0000-0000-0000-000000000001","type":"organization"}},"store":{"meta":{"href":"https://old/entity/store/00000000-0000-0000-0000-000000000002","type":"store"}},"supply":{"meta":{"href":"https://old/entity/supply/00000000-0000-0000-0000-000000000003","type":"supply"}}}""";

    private static string PositionJson() =>
        """{"assortment":{"meta":{"href":"https://old/entity/product/00000000-0000-0000-0000-000000000004","type":"product"}},"quantity":1,"price":100}""";

    private sealed class PreparationStub(IReadOnlyList<Guid> ready) : IPurchaseReturnPreparationService
    {
        public Task<PurchaseReturnPreparationResult> PrepareAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PurchaseReturnPreparationResult(ready, []));
    }

    private sealed class RecordingRepository(
        IReadOnlyDictionary<Guid, string> documents,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> positions) : IPurchaseReturnPreparationRepository
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) => Task.FromResult(documents);

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) => Task.FromResult(positions);

        public Task UpsertDocumentsAsync(Guid accountId, IReadOnlyDictionary<Guid, string> documents, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReplacePositionsAsync(Guid accountId, Guid purchaseReturnId, IReadOnlyDictionary<Guid, string> positions, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingGateway(List<string> events) : IMoySkladPurchaseReturnGateway
    {
        public Task<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>> GetAgentAccountsAsync(
            Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>>([]);

        public Task<IReadOnlyList<MoySkladPurchaseReturnContract>> GetContractsAsync(
            Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnContract>>([]);

        public Func<IReadOnlyList<Guid>, IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteResults { get; init; } =
            ids => ids.Select(id => new MoySkladPurchaseReturnBatchDeleteResult(id, true)).ToArray();

        public Func<IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem>, IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateResults { get; init; } =
            items => items.Select(item => new MoySkladPurchaseReturnBatchCreateResult(item.SourceDocumentId, Guid.NewGuid(), item.PayloadJson)).ToArray();

        public List<IReadOnlyList<Guid>> DeletedBatches { get; } = [];
        public List<IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem>> CreatedBatches { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId, string correlationId, IReadOnlyList<Guid> purchaseReturnIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteBatchAsync(Guid accountId, string correlationId, IReadOnlyList<Guid> purchaseReturnIds, CancellationToken cancellationToken)
        {
            events.Add("delete");
            DeletedBatches.Add(purchaseReturnIds);
            return Task.FromResult(DeleteResults(purchaseReturnIds));
        }

        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents, CancellationToken cancellationToken)
        {
            events.Add("create");
            CreatedBatches.Add(documents);
            return Task.FromResult(CreateResults(documents));
        }
    }
}
