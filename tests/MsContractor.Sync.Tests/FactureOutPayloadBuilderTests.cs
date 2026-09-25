using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.Sync.Tests;

public sealed class FactureOutPayloadBuilderTests
{
    [Fact]
    public async Task BuildAsync_RemovesTechnicalFieldsAndRewritesAgent()
    {
        var accountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var rawJson = $$"""
            {
              "meta": { "type": "factureout", "keep": true },
              "created": "created",
              "deleted": false,
              "id": "{{documentId:D}}",
              "printed": false,
              "published": true,
              "sum": 100,
              "updated": "updated",
              "syncId": "{{Guid.NewGuid():D}}",
              "accountId": "account-id",
              "agent": {
                "meta": {
                  "href": "https://api.test/api/remap/1.2/entity/counterparty/{{Guid.NewGuid():D}}",
                  "type": "counterparty"
                }
              },
              "nested": { "meta": { "keep": true } },
              "name": "kept"
            }
            """;
        var recreationItems = new RecordingFactureOutRecreationItemRepository();
        var builder = new FactureOutPayloadBuilder(
            new RawDataRepository(new Dictionary<Guid, string> { [documentId] = rawJson }),
            recreationItems);

        var result = await builder.BuildAsync(
            accountId,
            mainCounterpartyId,
            [documentId],
            CancellationToken.None);

        var builtPayload = Assert.Single(result.Payloads);
        var payload = JsonNode.Parse(builtPayload.PayloadJson)!.AsObject();
        Assert.Empty(result.SkippedDocuments);
        Assert.Equal("account-id", payload["accountId"]!.GetValue<string>());
        Assert.Equal("kept", payload["name"]!.GetValue<string>());
        Assert.True(payload["nested"]!["meta"]!["keep"]!.GetValue<bool>());

        foreach (var field in new[]
                 { "meta", "created", "deleted", "id", "printed", "published", "sum", "updated" })
            Assert.False(payload.ContainsKey(field), $"Field {field} should be removed.");
        Assert.True(Guid.TryParse(payload["syncId"]!.GetValue<string>(), out var newSyncId));
        Assert.NotEqual(Guid.Empty, newSyncId);
        Assert.Equal(newSyncId, recreationItems.NewSyncIds[documentId]);
        Assert.Equal(new[] { documentId }, recreationItems.CreatePayloadIds);

        var agent = payload["agent"]!.AsObject();
        Assert.Equal("counterparty", agent["meta"]!["type"]!.GetValue<string>());
        Assert.EndsWith(
            $"/entity/counterparty/{mainCounterpartyId:D}",
            agent["meta"]!["href"]!.GetValue<string>());
    }

    [Fact]
    public async Task BuildAsync_SkipsOnlyDocumentsWithMissingOrInvalidRawData()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var validId = Guid.NewGuid();
        var malformedId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var recreationItems = new RecordingFactureOutRecreationItemRepository();
        var builder = new FactureOutPayloadBuilder(
            new RawDataRepository(new Dictionary<Guid, string>
            {
                [validId] = "{\"agent\":{\"meta\":{\"href\":\"https://api.test/entity/counterparty/" +
                    Guid.NewGuid().ToString("D") + "\"}}}",
                [malformedId] = "{malformed"
            }),
            recreationItems);

        var result = await builder.BuildAsync(
            accountId,
            mainCounterpartyId,
            [validId, malformedId, missingId],
            CancellationToken.None);

        Assert.Equal(validId, Assert.Single(result.Payloads).SourceDocumentId);
        Assert.Equal(
            new[] { malformedId, missingId },
            result.SkippedDocuments.Select(item => item.DocumentId));
        Assert.All(result.SkippedDocuments, item =>
        {
            Assert.Equal("Skipped", item.Status);
            Assert.Equal("FACTUREOUT_PAYLOAD_INVALID", item.ErrorCode);
        });
        Assert.Equal(new[] { validId }, recreationItems.NewSyncIds.Keys);
        Assert.Equal(new[] { malformedId, missingId }, recreationItems.SkippedDocumentIds);
    }

    private sealed class RawDataRepository(IReadOnlyDictionary<Guid, string> documents)
        : IFactureOutRawDataRepository
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> documentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                documents.Where(item => documentIds.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value));

        public Task UpsertAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, string> documents,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingFactureOutRecreationItemRepository : IFactureOutRecreationItemRepository
    {
        public Dictionary<Guid, Guid> NewSyncIds { get; } = [];
        public List<Guid> SkippedDocumentIds { get; } = [];
        public List<Guid> CreatePayloadIds { get; } = [];

        public Task UpsertPreparationAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid?> preparedSourceSyncIds,
            IReadOnlyCollection<Guid> skippedDocumentIds,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SavePayloadBuildResultsAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid> newSyncIds,
            IReadOnlyCollection<Guid> skippedDocumentIds,
            CancellationToken cancellationToken)
        {
            foreach (var (documentId, newSyncId) in newSyncIds)
            {
                NewSyncIds[documentId] = newSyncId;
                CreatePayloadIds.Add(documentId);
            }

            SkippedDocumentIds.AddRange(skippedDocumentIds);
            return Task.CompletedTask;
        }

        public Task SaveRecreationResultsAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, Guid> transferredDocumentIds,
            IReadOnlyCollection<Guid> failedDocumentIds,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class FactureOutRecreationOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_PassesOnlySuccessfulPayloadsToRecreatorInBatches()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var documentIds = Enumerable.Range(0, 1001)
            .Select(_ => Guid.NewGuid())
            .ToArray();
        var skippedId = Guid.NewGuid();
        var payloadSkippedId = documentIds[0];
        var preparation = new PreparationService(documentIds, skippedId);
        var builder = new RecordingPayloadBuilder(payloadSkippedId);
        var recreator = new RecordingDocumentRecreationService();
        var orchestrator = new FactureOutRecreationOrchestrator(preparation, builder, recreator);

        var result = await orchestrator.ExecuteAsync(
            accountId,
            mainCounterpartyId,
            documentIds.Append(skippedId).ToArray(),
            CancellationToken.None);

        Assert.Equal(new[] { 1000, 1 }, builder.BatchSizes);
        Assert.Equal(documentIds, builder.DocumentIds);
        Assert.Equal(new[] { 999, 1 }, recreator.Calls.Select(call => call.DocumentsPayload.Count));
        Assert.Equal(
            documentIds.Where(id => id != payloadSkippedId),
            recreator.Calls.SelectMany(call => call.DocumentsPayload)
                .Select(payload => payload.SourceDocumentId));
        Assert.All(recreator.Calls.SelectMany(call => call.DocumentsPayload), payload =>
            Assert.Equal(
                $"{{\"sourceDocumentId\":\"{payload.SourceDocumentId:D}\"}}",
                payload.PayloadJson));
        Assert.All(recreator.Calls, call =>
        {
            Assert.Equal(accountId, call.AccountId);
            Assert.Equal(mainCounterpartyId, call.MainCounterpartyId);
        });
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == skippedId);
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == payloadSkippedId);
        Assert.Empty(result.TransferredDocumentIds);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotInvokeRecreatorWhenAllPayloadsAreSkipped()
    {
        var documentId = Guid.NewGuid();
        var preparationSkippedId = Guid.NewGuid();
        var preparation = new PreparationService([documentId], preparationSkippedId);
        var builder = new RecordingPayloadBuilder(skipAll: true);
        var recreator = new RecordingDocumentRecreationService();
        var orchestrator = new FactureOutRecreationOrchestrator(preparation, builder, recreator);

        var result = await orchestrator.ExecuteAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            [documentId, preparationSkippedId],
            CancellationToken.None);

        Assert.Empty(recreator.Calls);
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == documentId);
        Assert.Contains(result.SkippedDocuments, item => item.DocumentId == preparationSkippedId);
        Assert.Empty(result.TransferredDocumentIds);
    }

    private sealed class PreparationService(
        IReadOnlyList<Guid> documentIds,
        Guid skippedId) : IFactureOutPreparationService
    {
        public Task<FactureOutPreparationResult> PrepareAsync(
            Guid accountId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FactureOutPreparationResult(
                documentIds,
                [new FactureOutSkippedDocumentResult(
                    skippedId,
                    "Skipped",
                    "FACTUREOUT_NOT_FOUND",
                    "not found")]));
    }

    private sealed class RecordingPayloadBuilder(
        Guid? skippedDocumentId = null,
        bool skipAll = false) : IFactureOutPayloadBuilder
    {
        public List<int> BatchSizes { get; } = [];
        public List<Guid> DocumentIds { get; } = [];

        public Task<FactureOutPayloadBuildResult> BuildAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyCollection<Guid> factureOutIds,
            CancellationToken cancellationToken)
        {
            BatchSizes.Add(factureOutIds.Count);
            DocumentIds.AddRange(factureOutIds);

            var payloads = factureOutIds
                .Where(id => !skipAll && id != skippedDocumentId)
                .Select(id => new FactureOutPayload(
                    id,
                    $"{{\"sourceDocumentId\":\"{id:D}\"}}"))
                .ToArray();
            var skipped = factureOutIds
                .Where(id => skipAll || id == skippedDocumentId)
                .Select(id => new FactureOutSkippedDocumentResult(
                    id,
                    "Skipped",
                    "FACTUREOUT_PAYLOAD_INVALID",
                    "invalid payload"))
                .ToArray();

            return Task.FromResult(new FactureOutPayloadBuildResult(payloads, skipped));
        }
    }

    private sealed class RecordingDocumentRecreationService : IFactureOutDocumentRecreationService
    {
        public List<(
            Guid AccountId,
            Guid MainCounterpartyId,
            IReadOnlyList<FactureOutPayload> DocumentsPayload)> Calls { get; } = [];

        public Task<FactureOutDocumentRecreationResult> RecreateAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<FactureOutPayload> documentsPayload,
            CancellationToken cancellationToken)
        {
            Calls.Add((accountId, mainCounterpartyId, documentsPayload.ToArray()));
            return Task.FromResult(new FactureOutDocumentRecreationResult([], []));
        }
    }
}
