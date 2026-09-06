using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.Models.Exceptions;
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
                        logger.LogError(
                            "MoySklad document counterparty item failed: account_id={AccountId}, merge_job_id={MergeJobId}, merge_operation_id={OperationId}, correlation_id={CorrelationId}, http_method={HttpMethod}, entity_type={DocumentType}, entity_id={DocumentId}, http_status={StatusCode}, error_code={ErrorCode}, error_message={ErrorMessage}, retryable={Retryable}",
                            accountId, mergeJobId, operationId, correlationId,
                            chunk.Length == 1 ? "PUT" : "POST",
                            failure.DocumentType, failure.DocumentId, failure.StatusCode, failure.Code,
                            failure.Message, failure.Retryable);
                    }
                }
                catch (EgressException exception)
                {
                    failures.AddRange(chunk.Select(item => new MoySkladDocumentChangeFailure(
                        item.DocumentType,
                        item.DocumentId,
                        exception.Code,
                        exception.SafeMessage,
                        exception.HttpStatus ?? exception.StatusCode,
                        exception.Retryable,
                        chunk.Length == 1
                            ? $"entity/{documentType}/{item.DocumentId:D}"
                            : $"entity/{documentType}/batch",
                        exception.MoySkladErrorCode,
                        exception.MoySkladErrorMessage,
                        exception.ValidationError)));
                    logger.Log(
                        exception.Retryable ? LogLevel.Warning : LogLevel.Error,
                        "MoySklad document counterparty chunk failed: account_id={AccountId}, merge_job_id={MergeJobId}, merge_operation_id={OperationId}, correlation_id={CorrelationId}, entity_type={DocumentType}, chunk_size={ChunkSize}, http_status={HttpStatus}, error_code={ErrorCode}, error_message={ErrorMessage}, moysklad_error_code={MoySkladErrorCode}, moysklad_error_message={MoySkladErrorMessage}, response_validation_error={ValidationError}, retryable={Retryable}",
                        accountId, mergeJobId, operationId, correlationId, documentType, chunk.Length,
                        exception.HttpStatus, exception.Code, exception.SafeMessage, exception.MoySkladErrorCode,
                        exception.MoySkladErrorMessage, exception.ValidationError, exception.Retryable);
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
