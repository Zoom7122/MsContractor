using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public interface IFactureOutRecreationOrchestrator
{
    Task<FactureOutRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutRecreationOrchestrator : IFactureOutRecreationOrchestrator
{
    private readonly IFactureOutPreparationService _preparationService;
    private readonly IFactureOutPayloadBuilder _payloadBuilder;
    private readonly IFactureOutDocumentRecreationService _documentRecreationService;

    public FactureOutRecreationOrchestrator(
        IFactureOutPreparationService preparationService,
        IFactureOutPayloadBuilder payloadBuilder,
        IFactureOutDocumentRecreationService documentRecreationService)
    {
        _preparationService = preparationService;
        _payloadBuilder = payloadBuilder;
        _documentRecreationService = documentRecreationService;
    }

    public async Task<FactureOutRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        var preparation = await _preparationService.PrepareAsync(
            accountId,
            factureOutIds,
            cancellationToken);

        var skipped = new List<FactureOutSkippedDocumentResult>(preparation.SkippedDocuments);
        var transferredDocumentIds = new List<Guid>();
        var transferredDocuments = new List<DocumentRecreationTransferredDocumentResult>();
        var createdWithErrors = new List<FactureOutCreatedWithErrorResult>();
        var unCreatedButDeleted = new List<DocumentRecreationUnCreatedButDeletedResult>();
        var failedDocuments = new List<FactureOutFailedDocumentResult>();
        foreach (var batch in preparation.DocumentIds.Chunk(1000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var documentsPayload = await BuildBatchAsync(
                accountId,
                mainCounterpartyId,
                batch,
                cancellationToken);
            skipped.AddRange(documentsPayload.SkippedDocuments);

            if (documentsPayload.Payloads.Count == 0)
                continue;

            var recreation = await _documentRecreationService.RecreateAsync(
                accountId,
                mainCounterpartyId,
                documentsPayload.Payloads,
                cancellationToken);
            transferredDocumentIds.AddRange(recreation.TransferredDocumentIds);
            transferredDocuments.AddRange(recreation.TransferredDocuments);
            skipped.AddRange(recreation.SkippedDocuments);
            createdWithErrors.AddRange(recreation.CreatedWithErrors);
            unCreatedButDeleted.AddRange(recreation.UnCreatedButDeleted);
            failedDocuments.AddRange(recreation.FailedDocuments);
        }

        return new FactureOutRecreationResult(
            transferredDocumentIds.Distinct().ToArray(),
            skipped,
            createdWithErrors,
            failedDocuments)
        {
            TransferredDocuments = transferredDocuments
                .DistinctBy(item => item.SourceDocumentId)
                .ToArray(),
            UnCreatedButDeleted = unCreatedButDeleted
                .DistinctBy(item => item.SourceDocumentId)
                .ToArray()
        };
    }

    private Task<FactureOutPayloadBuildResult> BuildBatchAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> batch,
        CancellationToken cancellationToken) =>
        _payloadBuilder.BuildAsync(
            accountId,
            mainCounterpartyId,
            batch,
            cancellationToken);
}
