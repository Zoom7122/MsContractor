using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Facturein;

public interface IFactureInPreparationService
{
    Task<FactureInPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureInIds,
        CancellationToken cancellationToken);
}

public sealed class FactureInPreparationService : IFactureInPreparationService
{
    private readonly IMoySkladFactureInGateway _gateway;
    private readonly IFactureInRawDataRepository _repository;
    private readonly ILogger<FactureInPreparationService> _logger;

    public FactureInPreparationService(
        IMoySkladFactureInGateway gateway,
        IFactureInRawDataRepository repository,
        ILogger<FactureInPreparationService> logger)
    {
        _gateway = gateway;
        _repository = repository;
        _logger = logger;
    }

    public async Task<FactureInPreparationResult> PrepareAsync(
        Guid accountId,
        IReadOnlyList<Guid> factureInIds,
        CancellationToken cancellationToken)
    {
        ValidateInput(accountId, factureInIds);
        var correlationId = Guid.NewGuid().ToString("D");
        var documents = new Dictionary<Guid, string>();
        var skipped = new List<FactureInSkippedDocumentResult>();

        foreach (var batch in factureInIds.Chunk(MoySkladFactureInGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchDocuments = await _gateway.GetAsync(
                accountId,
                Guid.Empty,
                correlationId,
                batch,
                cancellationToken);

            _logger.LogInformation(
                "facturein document batch received: account_id={AccountId}, requested_count={RequestedCount}, returned_count={ReturnedCount}, correlation_id={CorrelationId}",
                accountId,
                batch.Length,
                batchDocuments.Count,
                correlationId);

            await _repository.UpsertAsync(accountId, batchDocuments, cancellationToken);

            foreach (var document in batchDocuments)
            {
                if (!documents.TryAdd(document.Key, document.Value))
                {
                    throw new InvalidOperationException(
                        $"MoySklad returned duplicate facturein {document.Key:D} across batches.");
                }
            }

            skipped.AddRange(batch
                .Where(documentId => !batchDocuments.ContainsKey(documentId))
                .Select(documentId => new FactureInSkippedDocumentResult(
                    documentId,
                    "Skipped",
                    "FACTUREIN_NOT_FOUND",
                    "facturein was not returned by MoySklad.")));
        }

        return new FactureInPreparationResult(documents, skipped);
    }

    private static void ValidateInput(Guid accountId, IReadOnlyList<Guid> factureInIds)
    {
        if (accountId == Guid.Empty ||
            factureInIds.Count == 0 ||
            factureInIds.Any(id => id == Guid.Empty) ||
            factureInIds.Distinct().Count() != factureInIds.Count)
        {
            throw new ArgumentException(
                "A non-empty account and unique facturein ids are required.",
                nameof(factureInIds));
        }
    }
}
