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

        return new FactureInRecreationResult(
            payloads.CompletedDocuments.Select(item => item.SourceFactureInId)
                .Concat(transfer.TransferredDocumentIds).Distinct().ToArray(),
            preparation.SkippedDocuments.Concat(payloads.SkippedDocuments).ToArray(),
            [],
            payloads.FailedDocuments.Concat(transfer.FailedDocuments).ToArray());
    }
}
