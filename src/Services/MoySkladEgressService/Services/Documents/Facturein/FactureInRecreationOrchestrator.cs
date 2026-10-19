using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Facturein;

public interface IFactureInRecreationOrchestrator
{
    Task<FactureInRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureInIds,
        CancellationToken cancellationToken);
}

public sealed class FactureInRecreationOrchestrator : IFactureInRecreationOrchestrator
{
    private readonly IFactureInPreparationService _preparationService;
    private readonly IFactureInPayloadBuilder _payloadBuilder;
    private readonly IFactureInDocumentTransferService _transferService;
    private readonly IFactureInRecreationItemRepository _items;
    private readonly ILogger<FactureInRecreationOrchestrator> _logger;

    public FactureInRecreationOrchestrator(
        IFactureInPreparationService preparationService,
        IFactureInPayloadBuilder payloadBuilder,
        IFactureInDocumentTransferService transferService,
        IFactureInRecreationItemRepository items,
        ILogger<FactureInRecreationOrchestrator> logger)
    {
        _preparationService = preparationService;
        _payloadBuilder = payloadBuilder;
        _transferService = transferService;
        _items = items;
        _logger = logger;
    }

    public async Task<FactureInRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureInIds,
        CancellationToken cancellationToken)
    {
        var existing = await _items.GetAsync(accountId, factureInIds, cancellationToken);
        var sourceIds = factureInIds.Where(id => !existing.ContainsKey(id)).ToArray();
        var preparation = sourceIds.Length == 0
            ? new FactureInPreparationResult(new Dictionary<Guid, string>(), [])
            : await _preparationService.PrepareAsync(accountId, sourceIds, cancellationToken);
        var payloads = await _payloadBuilder.BuildAsync(accountId, mainCounterpartyId,
            preparation.Documents.Keys.Concat(existing.Keys).ToArray(), cancellationToken);
        var transfer = await _transferService.TransferAsync(accountId, payloads.PreparedDocuments, cancellationToken);

        _logger.LogInformation(
            "facturein preparation completed: account_id={AccountId}, main_counterparty_id={MainCounterpartyId}, prepared_count={PreparedCount}, skipped_count={SkippedCount}",
            accountId,
            mainCounterpartyId,
            preparation.Documents.Count,
            preparation.SkippedDocuments.Count + payloads.SkippedDocuments.Count);

        var failedDocuments = payloads.FailedDocuments.Concat(transfer.FailedDocuments).ToArray();
        var unCreatedButDeleted = payloads.UnCreatedButDeleted
            .Concat(transfer.UnCreatedButDeleted)
            .DistinctBy(item => item.SourceDocumentId)
            .ToArray();
        var unCreatedIds = unCreatedButDeleted.Select(item => item.SourceDocumentId).ToHashSet();
        var createdWithErrors = payloads.CreatedWithErrors
            .Concat(transfer.CreatedWithErrors)
            .DistinctBy(item => item.SourceDocumentId)
            .ToArray();
        var createdWithErrorIds = createdWithErrors.Select(item => item.SourceDocumentId).ToHashSet();
        var transferredDocuments = payloads.CompletedDocuments
            .Where(item => item.NewFactureInId is not null)
            .Select(item => new DocumentRecreationTransferredDocumentResult(
                item.SourceFactureInId,
                item.NewFactureInId!.Value))
            .Concat(transfer.TransferredDocuments)
            .DistinctBy(item => item.SourceDocumentId)
            .ToArray();
        var transferredIds = transferredDocuments.Select(item => item.SourceDocumentId).ToHashSet();
        var skippedDocuments = preparation.SkippedDocuments
            .Concat(payloads.SkippedDocuments)
            .Concat(failedDocuments
                .Where(item => !transferredIds.Contains(item.SourceDocumentId) &&
                               !createdWithErrorIds.Contains(item.SourceDocumentId) &&
                               !unCreatedIds.Contains(item.SourceDocumentId))
                .Select(item => new FactureInSkippedDocumentResult(
                    item.SourceDocumentId,
                    "Skipped",
                    item.ErrorCode,
                    item.Error)))
            .DistinctBy(item => item.DocumentId)
            .ToArray();

        return new FactureInRecreationResult(
            transferredDocuments.Select(item => item.SourceDocumentId).ToArray(),
            skippedDocuments,
            createdWithErrors,
            failedDocuments)
        {
            TransferredDocuments = transferredDocuments,
            UnCreatedButDeleted = unCreatedButDeleted
        };
    }
}
