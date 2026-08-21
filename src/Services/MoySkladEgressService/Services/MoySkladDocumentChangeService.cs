using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Services;

public interface IMoySkladDocumentChangeService
{
    Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        MoySkladDocumentChangeCounterpartyRequest request,
        CancellationToken cancellationToken);
}

public sealed class MoySkladDocumentChangeService(
    IMoySkladDocumentGateway gateway,
    MoySkladDocumentChangeOptions options,
    ILogger<MoySkladDocumentChangeService> logger) : IMoySkladDocumentChangeService
{
    public const int BatchSize = 1000;

    public async Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid mergeJobId,
        Guid operationId,
        string correlationId,
        MoySkladDocumentChangeCounterpartyRequest request,
        CancellationToken cancellationToken)
    {
        var documents = request.Documents!;
        var configuredTypes = options.DocumentTypes.ToHashSet(StringComparer.Ordinal);
        var changed = new List<MoySkladDocumentChangeItem>();
        var skipped = documents
            .Where(item => !configuredTypes.Contains(item.DocumentType))
            .Select(item => new MoySkladDocumentChangeSkippedItem(
                item.DocumentType,
                item.DocumentId,
                "DOCUMENT_TYPE_NOT_CONFIGURED"))
            .ToList();
        var failures = new List<MoySkladDocumentChangeFailure>();

        foreach (var documentType in options.DocumentTypes)
        {
            var typedDocuments = documents
                .Where(item => string.Equals(item.DocumentType, documentType, StringComparison.Ordinal))
                .ToArray();
            foreach (var chunk in typedDocuments.Chunk(BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await gateway.ChangeCounterpartyAsync(
                        accountId,
                        requestedByUserId,
                        mergeJobId,
                        operationId,
                        correlationId,
                        request.MainCounterpartyId,
                        documentType,
                        chunk,
                        cancellationToken);
                    changed.AddRange(result.ChangedDocuments);
                    failures.AddRange(result.Failures);
                    foreach (var failure in result.Failures)
                    {
                        logger.LogWarning(
                            "MoySklad document counterparty item failed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, document_id={DocumentId}, status={StatusCode}, error_code={ErrorCode}, error_message={ErrorMessage}",
                            accountId, mergeJobId, operationId, correlationId, failure.DocumentType,
                            failure.DocumentId, failure.StatusCode, failure.Code, failure.Message);
                    }
                }
                catch (EgressException exception)
                {
                    var retryable = exception.StatusCode == 429 || exception.StatusCode >= 500;
                    failures.AddRange(chunk.Select(item => new MoySkladDocumentChangeFailure(
                        item.DocumentType,
                        item.DocumentId,
                        exception.Code,
                        exception.SafeMessage,
                        exception.StatusCode,
                        retryable)));
                    logger.LogWarning(
                        "MoySklad document counterparty chunk failed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, correlation_id={CorrelationId}, document_type={DocumentType}, chunk_size={ChunkSize}, error_code={ErrorCode}, error_message={ErrorMessage}",
                        accountId, mergeJobId, operationId, correlationId, documentType, chunk.Length,
                        exception.Code, exception.SafeMessage);
                }
            }
        }

        return new MoySkladDocumentChangeCounterpartyResponse(
            request.MainCounterpartyId,
            documents.Count,
            changed.Count,
            skipped.Count,
            failures.Count,
            changed,
            skipped,
            failures);
    }
}
