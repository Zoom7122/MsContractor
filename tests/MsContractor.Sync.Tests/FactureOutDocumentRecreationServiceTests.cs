using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.Sync.Tests;

public sealed class FactureOutDocumentRecreationServiceTests
{
    [Fact]
    public async Task DeleteDocumentsAsync_ReturnsOnlySuccessfullyDeletedDocumentsAndFailures()
    {
        var first = Payload();
        var rejected = Payload();
        var missing = Payload();
        var accountId = Guid.NewGuid();
        var correlationId = "correlation-1";
        var gateway = new RecordingGateway
        {
            DeleteResults =
            [
                new MoySkladFactureOutBatchDeleteResult(first.SourceDocumentId, true),
                new MoySkladFactureOutBatchDeleteResult(
                    rejected.SourceDocumentId,
                    false,
                    "DELETE_REJECTED",
                    "Delete was rejected.")
            ]
        };
        var service = Service(gateway);

        var result = await service.DeleteDocumentsAsync(
            accountId,
            correlationId,
            [first, rejected, missing],
            CancellationToken.None);

        Assert.Equal([first], result.ReadyToCreatePayloads);
        Assert.Collection(
            result.FailedDocuments,
            item =>
            {
                Assert.Equal(rejected.SourceDocumentId, item.SourceDocumentId);
                Assert.Equal("DELETE_REJECTED", item.ErrorCode);
                Assert.Equal("Delete was rejected.", item.Error);
            },
            item =>
            {
                Assert.Equal(missing.SourceDocumentId, item.SourceDocumentId);
                Assert.Equal("FACTUREOUT_DELETE_RESPONSE_INVALID", item.ErrorCode);
            });
        Assert.Equal(accountId, gateway.DeleteAccountId);
        Assert.Equal(correlationId, gateway.DeleteCorrelationId);
        Assert.Equal(
            new[] { first.SourceDocumentId, rejected.SourceDocumentId, missing.SourceDocumentId },
            gateway.DeletedDocumentIds);
    }

    [Fact]
    public async Task CreateAndVerifyDocumentsAsync_MapsByNewSyncIdAndRetriesOnlyMissingDocuments()
    {
        var first = Payload();
        var second = Payload();
        var missingOnFirstAttempt = Payload();
        var deletionFailure = Failed(Payload(), "FACTUREOUT_DELETE_FAILED", "Delete failed.");
        var firstNewDocumentId = Guid.NewGuid();
        var secondNewDocumentId = Guid.NewGuid();
        var thirdNewDocumentId = Guid.NewGuid();
        var gateway = new RecordingGateway();
        gateway.CreateResponses.Enqueue(
        [
            CreateResult(second, secondNewDocumentId, responseIndex: 0),
            CreateResult(first, firstNewDocumentId, responseIndex: 1)
        ]);
        gateway.CreateResponses.Enqueue([CreateResult(missingOnFirstAttempt, thirdNewDocumentId)]);
        var repository = new RecordingRepository();
        var service = Service(gateway, repository);

        var result = await service.CreateAndVerifyDocumentsAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-2",
            [first, second, missingOnFirstAttempt],
            [deletionFailure],
            CancellationToken.None);

        Assert.Equal(
            new[] { first.SourceDocumentId, second.SourceDocumentId, missingOnFirstAttempt.SourceDocumentId },
            result.TransferredDocumentIds);
        Assert.Equal(new[] { deletionFailure }, result.FailedDocuments);
        Assert.Equal(2, gateway.CreatedBatches.Count);
        Assert.Equal(
            new[] { first.SourceDocumentId, second.SourceDocumentId, missingOnFirstAttempt.SourceDocumentId },
            gateway.CreatedBatches[0].Select(item => item.SourceDocumentId));
        Assert.Equal([missingOnFirstAttempt.SourceDocumentId],
            gateway.CreatedBatches[1].Select(item => item.SourceDocumentId));
        Assert.All(gateway.CreateCorrelationIds, id => Assert.Equal("correlation-2", id));
        Assert.Equal(
            new Dictionary<Guid, Guid>
            {
                [first.SourceDocumentId] = firstNewDocumentId,
                [second.SourceDocumentId] = secondNewDocumentId,
                [missingOnFirstAttempt.SourceDocumentId] = thirdNewDocumentId
            },
            repository.TransferredDocumentIds);
        Assert.Equal([deletionFailure.SourceDocumentId], repository.FailedDocumentIds);
    }

    [Fact]
    public async Task RecreateAsync_CreatesOnlyDeletedDocumentsAndPersistsBothStageFailures()
    {
        var first = Payload();
        var deleteRejected = Payload();
        var createRejected = Payload();
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var newDocumentId = Guid.NewGuid();
        var events = new List<string>();
        var gateway = new RecordingGateway(events)
        {
            DeleteResults =
            [
                new MoySkladFactureOutBatchDeleteResult(first.SourceDocumentId, true),
                new MoySkladFactureOutBatchDeleteResult(
                    deleteRejected.SourceDocumentId,
                    false,
                    "DELETE_REJECTED",
                    "Delete was rejected."),
                new MoySkladFactureOutBatchDeleteResult(createRejected.SourceDocumentId, true)
            ]
        };
        gateway.CreateResponses.Enqueue([CreateResult(first, newDocumentId)]);
        gateway.CreateResponses.Enqueue(
        [
            new MoySkladFactureOutBatchCreateResult(
                0,
                null,
                createRejected.NewSyncId,
                "factureout",
                "CREATE_REJECTED",
                "Create was rejected.")
        ]);
        var repository = new RecordingRepository(events);
        var service = Service(gateway, repository);

        var result = await service.RecreateAsync(
            accountId,
            mainCounterpartyId,
            [first, deleteRejected, createRejected],
            CancellationToken.None);

        Assert.Equal(["delete", "create", "create", "save"], events);
        Assert.Equal(
            new[] { first.SourceDocumentId, createRejected.SourceDocumentId },
            gateway.CreatedBatches[0].Select(item => item.SourceDocumentId));
        Assert.Equal(
            new[] { createRejected.SourceDocumentId },
            gateway.CreatedBatches[1].Select(item => item.SourceDocumentId));
        Assert.Equal(
            gateway.CreateCorrelationIds[0],
            gateway.CreateCorrelationIds[1]);
        Assert.Equal(gateway.DeleteCorrelationId, gateway.CreateCorrelationIds[0]);
        Assert.Equal([first.SourceDocumentId], result.TransferredDocumentIds);
        Assert.Collection(
            result.FailedDocuments,
            item =>
            {
                Assert.Equal(deleteRejected.SourceDocumentId, item.SourceDocumentId);
                Assert.Equal("DELETE_REJECTED", item.ErrorCode);
            },
            item =>
            {
                Assert.Equal(createRejected.SourceDocumentId, item.SourceDocumentId);
                Assert.Equal("CREATE_REJECTED", item.ErrorCode);
                Assert.Equal("Create was rejected.", item.Error);
            });
        Assert.Equal(
            new Dictionary<Guid, Guid> { [first.SourceDocumentId] = newDocumentId },
            repository.TransferredDocumentIds);
        Assert.Equal(
            new[] { deleteRejected.SourceDocumentId, createRejected.SourceDocumentId },
            repository.FailedDocumentIds);
    }

    [Fact]
    public async Task RecreateAsync_EmptyInputKeepsExistingNoOpBehavior()
    {
        var gateway = new RecordingGateway();
        var repository = new RecordingRepository();
        var service = Service(gateway, repository);

        var result = await service.RecreateAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [],
            CancellationToken.None);

        Assert.Empty(result.TransferredDocumentIds);
        Assert.Empty(result.FailedDocuments);
        Assert.Empty(gateway.DeletedDocumentIds);
        Assert.Empty(gateway.CreatedBatches);
        Assert.Equal(0, repository.SaveCallCount);
    }

    private static FactureOutDocumentRecreationService Service(
        RecordingGateway gateway,
        RecordingRepository? repository = null) =>
        new(gateway, new FactureOutDocumentRecreationVerifier(), repository ?? new RecordingRepository());

    private static FactureOutPayload Payload()
    {
        return new FactureOutPayload(Guid.NewGuid(), "{\"name\":\"factureout\"}")
        {
            NewSyncId = Guid.NewGuid()
        };
    }

    private static FactureOutFailedDocumentResult Failed(
        FactureOutPayload payload,
        string errorCode,
        string error) =>
        new(payload.SourceDocumentId, payload.NewSyncId, null, "Failed", errorCode, error);

    private static MoySkladFactureOutBatchCreateResult CreateResult(
        FactureOutPayload payload,
        Guid documentId,
        int responseIndex = 0) =>
        new(responseIndex, documentId, payload.NewSyncId, "factureout");

    private sealed class RecordingGateway : IMoySkladFactureOutGateway
    {
        private readonly List<string>? _events;

        public RecordingGateway(List<string>? events = null) => _events = events;

        public IReadOnlyList<MoySkladFactureOutBatchDeleteResult> DeleteResults { get; init; } = [];
        public Queue<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateResponses { get; } = new();
        public List<Guid> DeletedDocumentIds { get; } = [];
        public List<IReadOnlyList<MoySkladFactureOutBatchCreateItem>> CreatedBatches { get; } = [];
        public List<string> CreateCorrelationIds { get; } = [];
        public Guid? DeleteAccountId { get; private set; }
        public string? DeleteCorrelationId { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<MoySkladFactureOutBatchDeleteResult>> DeleteBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken)
        {
            _events?.Add("delete");
            DeleteAccountId = accountId;
            DeleteCorrelationId = correlationId;
            DeletedDocumentIds.AddRange(factureOutIds);
            return Task.FromResult(DeleteResults);
        }

        public Task<IReadOnlyList<MoySkladFactureOutBatchCreateResult>> CreateBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<MoySkladFactureOutBatchCreateItem> documents,
            CancellationToken cancellationToken)
        {
            _events?.Add("create");
            CreateCorrelationIds.Add(correlationId);
            CreatedBatches.Add(documents);
            return Task.FromResult(CreateResponses.Dequeue());
        }
    }

    private sealed class RecordingRepository : IFactureOutRecreationItemRepository
    {
        private readonly List<string>? _events;

        public RecordingRepository(List<string>? events = null) => _events = events;

        public int SaveCallCount { get; private set; }
        public IReadOnlyDictionary<Guid, Guid> TransferredDocumentIds { get; private set; } =
            new Dictionary<Guid, Guid>();
        public IReadOnlyCollection<Guid> FailedDocumentIds { get; private set; } = [];

        public Task UpsertPreparationAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid?> sourceSyncIds,
            IReadOnlyCollection<Guid> skippedDocumentIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SavePayloadBuildResultsAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid> newSyncIds,
            IReadOnlyCollection<Guid> skippedDocumentIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveRecreationResultsAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid> transferredDocumentIds,
            IReadOnlyCollection<Guid> failedDocumentIds,
            CancellationToken cancellationToken)
        {
            _events?.Add("save");
            SaveCallCount++;
            TransferredDocumentIds = new Dictionary<Guid, Guid>(transferredDocumentIds);
            FailedDocumentIds = failedDocumentIds.ToArray();
            return Task.CompletedTask;
        }
    }
}
