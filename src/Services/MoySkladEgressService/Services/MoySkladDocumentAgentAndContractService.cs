using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Services;

public interface IMoySkladDocumentAgentAndContractService
{
    Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
        Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
        MoySkladDocumentChangeAgentAndContractRequest request, CancellationToken cancellationToken);
}

public sealed class MoySkladDocumentAgentAndContractService(
    IMoySkladDocumentGateway gateway,
    MoySkladDocumentAgentAndContractOptions options,
    ILogger<MoySkladDocumentAgentAndContractService> logger) : IMoySkladDocumentAgentAndContractService
{
    public async Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
        Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
        MoySkladDocumentChangeAgentAndContractRequest request, CancellationToken cancellationToken)
    {
        var documents = request.Documents!;
        var configuredTypes = options.DocumentTypes.ToHashSet(StringComparer.Ordinal);
        var skipped = documents.Where(item => !configuredTypes.Contains(item.DocumentType))
            .Select(item => new MoySkladDocumentChangeSkippedItem(item.DocumentType, item.DocumentId,
                "DOCUMENT_TYPE_NOT_CONFIGURED")).ToList();
        var processable = documents.Where(item => configuredTypes.Contains(item.DocumentType)).ToArray();
        var failures = new List<MoySkladDocumentChangeFailure>();
        var changed = new List<MoySkladDocumentChangeItem>();
        var failedContracts = new Dictionary<Guid, MoySkladDocumentChangeFailure>();
        var contractIds = processable.Where(item => item.Contract is not null)
            .Select(item => item.Contract!.Value).Distinct().ToArray();

        foreach (var chunk in contractIds.Chunk(MoySkladDocumentChangeService.BatchSize))
        {
            try
            {
                var result = await gateway.ChangeContractAgentsAsync(
                    accountId, requestedByUserId, mergeJobId, operationId, correlationId,
                    request.MainCounterpartyId, chunk, cancellationToken);
                foreach (var failure in result.Failures)
                    failedContracts[failure.DocumentId] = failure;
            }
            catch (EgressException exception)
            {
                foreach (var contractId in chunk)
                    failedContracts[contractId] = new MoySkladDocumentChangeFailure(
                        "contract", contractId, exception.Code, exception.SafeMessage,
                        exception.HttpStatus ?? exception.StatusCode, exception.Retryable,
                        chunk.Length == 1 ? $"entity/contract/{contractId:D}" : "entity/contract/batch",
                        exception.MoySkladErrorCode, exception.MoySkladErrorMessage, exception.ValidationError);
            }
        }

        foreach (var document in processable.Where(item => item.Contract is { } contract && failedContracts.ContainsKey(contract)))
        {
            var contractFailure = failedContracts[document.Contract!.Value];
            failures.Add(new MoySkladDocumentChangeFailure(
                document.DocumentType, document.DocumentId, contractFailure.Code,
                $"Contract {document.Contract:D} was not updated: {contractFailure.Message}",
                contractFailure.StatusCode, contractFailure.Retryable, contractFailure.Endpoint,
                contractFailure.MoySkladErrorCode, contractFailure.MoySkladErrorMessage, contractFailure.ValidationError));
        }

        var blockedDocuments = failures.Select(item => item.DocumentId).ToHashSet();
        foreach (var documentType in options.DocumentTypes)
        {
            var typedDocuments = processable.Where(item =>
                string.Equals(item.DocumentType, documentType, StringComparison.Ordinal) &&
                !blockedDocuments.Contains(item.DocumentId)).ToArray();
            foreach (var chunk in typedDocuments.Chunk(MoySkladDocumentChangeService.BatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var result = await gateway.ChangeAgentAndContractAsync(
                        accountId, requestedByUserId, mergeJobId, operationId, correlationId,
                        request.MainCounterpartyId, documentType, chunk, cancellationToken);
                    changed.AddRange(result.ChangedDocuments);
                    failures.AddRange(result.Failures);
                }
                catch (EgressException exception)
                {
                    failures.AddRange(chunk.Select(item => new MoySkladDocumentChangeFailure(
                        item.DocumentType, item.DocumentId, exception.Code, exception.SafeMessage,
                        exception.HttpStatus ?? exception.StatusCode, exception.Retryable,
                        chunk.Length == 1 ? $"entity/{documentType}/{item.DocumentId:D}" : $"entity/{documentType}/batch",
                        exception.MoySkladErrorCode, exception.MoySkladErrorMessage, exception.ValidationError)));
                    logger.Log(exception.Retryable ? LogLevel.Warning : LogLevel.Error,
                        "MoySklad document agent and contract chunk failed: account_id={AccountId}, merge_job_id={MergeJobId}, merge_operation_id={OperationId}, correlation_id={CorrelationId}, entity_type={DocumentType}, chunk_size={ChunkSize}, error_code={ErrorCode}",
                        accountId, mergeJobId, operationId, correlationId, documentType, chunk.Length, exception.Code);
                }
            }
        }

        return new MoySkladDocumentChangeCounterpartyResponse(
            request.MainCounterpartyId, documents.Count, changed.Count, skipped.Count, failures.Count,
            changed, skipped, failures);
    }
}
