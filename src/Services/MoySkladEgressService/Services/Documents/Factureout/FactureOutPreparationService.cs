using System.Text.Json;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public interface IFactureOutPreparationService
{
    Task<FactureOutPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutPreparationService : IFactureOutPreparationService
{
    private readonly IMoySkladFactureOutGateway _gateway;
    private readonly IFactureOutRawDataRepository _repository;
    private readonly IFactureOutRecreationItemRepository _recreationItems;
    private readonly ILogger<FactureOutPreparationService> _logger;

    public FactureOutPreparationService(
        IMoySkladFactureOutGateway gateway,
        IFactureOutRawDataRepository repository,
        IFactureOutRecreationItemRepository recreationItems,
        ILogger<FactureOutPreparationService> logger)
    {
        _gateway = gateway;
        _repository = repository;
        _recreationItems = recreationItems;
        _logger = logger;
    }

    public async Task<FactureOutPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, factureOutIds);

        var correlationId = Guid.NewGuid().ToString("D");
        var documentIds = new HashSet<Guid>();
        var skipped = new List<FactureOutSkippedDocumentResult>();

        foreach (var batch in factureOutIds.Chunk(MoySkladFactureOutGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchDocuments = await _gateway.GetAsync(
                accountId,
                correlationId,
                batch,
                cancellationToken);

            await _repository.UpsertAsync(accountId, batchDocuments, cancellationToken);

            var preparedSourceSyncIds = batchDocuments.ToDictionary(
                document => document.Key,
                document => ReadSourceSyncId(document.Value));
            var skippedIds = batch
                .Where(documentId => !batchDocuments.ContainsKey(documentId))
                .ToArray();
            await _recreationItems.UpsertPreparationAsync(
                accountId,
                preparedSourceSyncIds,
                skippedIds,
                cancellationToken);

            foreach (var document in batchDocuments)
            {
                if (!documentIds.Add(document.Key))
                {
                    throw new InvalidOperationException(
                        $"MoySklad returned duplicate factureout {document.Key:D} across batches.");
                }
            }

            skipped.AddRange(skippedIds
                .Select(documentId => new FactureOutSkippedDocumentResult(
                    documentId,
                    "Skipped",
                    "FACTUREOUT_NOT_FOUND",
                    "factureout was not returned by MoySklad.")));

            _logger.LogInformation(
                "factureout raw batch saved: account_id={AccountId}, requested_count={RequestedCount}, returned_count={ReturnedCount}, correlation_id={CorrelationId}",
                accountId,
                batch.Length,
                batchDocuments.Count,
                correlationId);
        }

        return new FactureOutPreparationResult(documentIds.ToArray(), skipped);
    }

    private static Guid? ReadSourceSyncId(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("syncId", out var syncIdElement) &&
                syncIdElement.ValueKind == JsonValueKind.String &&
                Guid.TryParse(syncIdElement.GetString(), out var syncId) &&
                syncId != Guid.Empty)
            {
                return syncId;
            }
        }
        catch (JsonException)
        {
            // The raw response has already been saved; an unavailable source sync id is optional.
        }

        return null;
    }

    private static void ValidateInput(Guid accountId, IReadOnlyList<Guid> factureOutIds)
    {
        if (accountId == Guid.Empty ||
            factureOutIds.Count == 0 ||
            factureOutIds.Any(id => id == Guid.Empty) ||
            factureOutIds.Distinct().Count() != factureOutIds.Count)
        {
            throw new ArgumentException(
                "A non-empty account and unique factureout ids are required.",
                nameof(factureOutIds));
        }
    }
}
