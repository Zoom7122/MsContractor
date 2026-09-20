using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

public interface IPurchaseReturnRecreationOrchestrator
{
    Task<PurchaseReturnVerificationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnRecreationOrchestrator : IPurchaseReturnRecreationOrchestrator
{
    private readonly IPurchaseReturnPreparationService _preparationService;
    private readonly IPurchaseReturnPreparationRepository _repository;
    private readonly IMoySkladPurchaseReturnGateway _gateway;
    private readonly IPurchaseReturnVerifier _verifier;
    private readonly PurchaseReturnCreateMapper _mapper;
    private readonly PurchaseReturnRecreationOptions _options;
    private readonly ILogger<PurchaseReturnRecreationOrchestrator> _logger;

    public PurchaseReturnRecreationOrchestrator(
        IPurchaseReturnPreparationService preparationService,
        IPurchaseReturnPreparationRepository repository,
        IMoySkladPurchaseReturnGateway gateway,
        IPurchaseReturnVerifier verifier,
        PurchaseReturnCreateMapper mapper,
        PurchaseReturnRecreationOptions options,
        ILogger<PurchaseReturnRecreationOrchestrator> logger)
    {
        _preparationService = preparationService;
        _repository = repository;
        _gateway = gateway;
        _verifier = verifier;
        _mapper = mapper;
        _options = options;
        _logger = logger;
    }

    public async Task<PurchaseReturnVerificationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var preparation = await _preparationService.PrepareAsync(
            accountId, mainCounterpartyId, purchaseReturnIds, cancellationToken);
        var results = preparation.Skipped
            .Select(item => new PurchaseReturnDocumentVerificationResult(
                item.PurchaseReturnId,
                null,
                "Skipped",
                [],
                [],
                [],
                "PURCHASERETURN_SKIPPED",
                item.Reason))
            .ToList();

        LogSkipped(preparation, accountId);
        if (preparation.ReadyForRecreationIds.Count == 0)
            return new PurchaseReturnVerificationResult(results);

        var documents = await _repository.GetRequiredDocumentsAsync(
            accountId, preparation.ReadyForRecreationIds, cancellationToken);
        var positions = await _repository.GetRequiredPositionsAsync(
            accountId, preparation.ReadyForRecreationIds, cancellationToken);

        var recreationReferences = await ResolveReferencesAsync(
            accountId, mainCounterpartyId, documents.Values, cancellationToken);
        var prepared = new List<PreparedPurchaseReturn>(preparation.ReadyForRecreationIds.Count);
        foreach (var purchaseReturnId in preparation.ReadyForRecreationIds)
        {
            try
            {
                var syncId = Guid.NewGuid();
                var references = ReferencesFor(documents[purchaseReturnId], recreationReferences);
                var payload = _mapper.BuildPayload(
                    documents[purchaseReturnId],
                    positions[purchaseReturnId],
                    mainCounterpartyId,
                    syncId,
                    references);
                prepared.Add(new PreparedPurchaseReturn(
                    purchaseReturnId,
                    syncId,
                    references,
                    payload));
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                results.Add(Failed(
                    purchaseReturnId,
                    "CreateFailed",
                    "PURCHASERETURN_PAYLOAD_INVALID",
                    exception.Message));
                _logger.LogWarning(
                    exception,
                    "Skipping purchasereturn because its create payload is invalid: account_id={AccountId}, purchasereturn_id={PurchaseReturnId}",
                    accountId,
                    purchaseReturnId);
            }
        }

        foreach (var batch in prepared.Chunk(MoySkladPurchaseReturnGateway.BatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batchItems = batch.ToArray();
            var deleteResult = await DeleteWithRetryAsync(
                accountId,
                batchItems.Select(item => item.SourceDocumentId).ToArray(),
                cancellationToken);
            var deleted = deleteResult.Deleted.ToHashSet();

            foreach (var item in batchItems.Where(item => !deleted.Contains(item.SourceDocumentId)))
            {
                deleteResult.Failed.TryGetValue(item.SourceDocumentId, out var failure);
                results.Add(Failed(
                    item.SourceDocumentId,
                    "DeleteFailed",
                    failure?.ErrorCode ?? "PURCHASERETURN_DELETE_FAILED",
                    failure?.Error ?? "The old purchasereturn was not deleted."));
            }

            var createItems = batchItems
                .Where(item => deleted.Contains(item.SourceDocumentId))
                .Select(item => new MoySkladPurchaseReturnBatchCreateItem(item.SourceDocumentId, item.PayloadJson))
                .ToArray();
            if (createItems.Length == 0)
                continue;

            var createPrepared = batchItems.Where(item => deleted.Contains(item.SourceDocumentId)).ToArray();
            var createResult = await CreateWithRetryAsync(accountId, createPrepared, createItems, cancellationToken);
            results.AddRange(createResult.Failed.Select(item => Failed(
                item.SourceDocumentId,
                "CreateFailed",
                item.ErrorCode,
                item.Error)));
            if (createResult.Created.Count == 0)
                continue;

            try
            {
                var verification = await _verifier.VerifyAsync(
                    accountId,
                    mainCounterpartyId,
                    createResult.Created,
                    cancellationToken);
                results.AddRange(verification.Documents);
                foreach (var document in verification.Documents.Where(item => item.Status != "Verified"))
                {
                    _logger.LogWarning(
                        "purchasereturn verification finished with status {Status}: account_id={AccountId}, source_id={SourceId}, new_id={NewId}, error_code={ErrorCode}, warnings={Warnings}, field_mismatches={FieldMismatches}, position_mismatches={PositionMismatches}",
                        document.Status,
                        accountId,
                        document.SourceDocumentId,
                        document.NewDocumentId,
                        document.ErrorCode,
                        string.Join(" | ", document.Warnings),
                        string.Join(" | ", document.FieldMismatches.Select(item =>
                            $"{item.Field}: expected={item.Expected ?? "<null>"}, actual={item.Actual ?? "<null>"}")),
                        string.Join(" | ", document.PositionMismatches.Select(item =>
                            $"{item.Kind}: expected={item.Expected ?? "<null>"}, actual={item.Actual ?? "<null>"}")));
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException or EgressException)
            {
                _logger.LogError(exception, "purchasereturn verification batch failed: account_id={AccountId}", accountId);
                results.AddRange(createResult.Created.Select(item => Failed(
                    item.SourceDocumentId,
                    "VerificationFailed",
                    "PURCHASERETURN_VERIFICATION_FAILED",
                    exception.Message)));
            }
        }

        return new PurchaseReturnVerificationResult(results);
    }

    private async Task<(IReadOnlyList<PurchaseReturnVerificationInput> Created,
        IReadOnlyList<CreateFailure> Failed)> CreateWithRetryAsync(
        Guid accountId,
        IReadOnlyList<PreparedPurchaseReturn> prepared,
        IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents,
        CancellationToken cancellationToken)
    {
        var pending = prepared.ToDictionary(item => item.SourceDocumentId);
        var created = new List<PurchaseReturnVerificationInput>();
        var failures = new Dictionary<Guid, CreateFailure>();

        for (var attempt = 1; pending.Count > 0 && attempt <= _options.MaxAttempts; attempt++)
        {
            IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult> response;
            try
            {
                var pendingItems = documents.Where(item => pending.ContainsKey(item.SourceDocumentId)).ToArray();
                response = await _gateway.CreateBatchAsync(
                    accountId, Guid.NewGuid().ToString("D"), pendingItems, cancellationToken);
            }
            catch (Exception exception) when (exception is EgressException or InvalidOperationException)
            {
                foreach (var item in pending.Values)
                    failures[item.SourceDocumentId] = new CreateFailure(
                        item.SourceDocumentId,
                        "PURCHASERETURN_CREATE_UNKNOWN_RESULT",
                        exception.Message);
                _logger.LogError(
                    exception,
                    "purchasereturn create batch failed without retry because the result is unknown: account_id={AccountId}, document_count={DocumentCount}",
                    accountId,
                    pending.Count);
                break;
            }

            var byId = response
                .GroupBy(item => item.SourceDocumentId)
                .ToDictionary(group => group.Key, group => group.Last());
            var retry = new Dictionary<Guid, PreparedPurchaseReturn>();
            var successfulIds = new List<Guid>();
            foreach (var item in pending.Values)
            {
                if (!byId.TryGetValue(item.SourceDocumentId, out var result))
                {
                    var errorCode = "PURCHASERETURN_CREATE_RESPONSE_INVALID";
                    var error = "MoySklad did not return an item result for the requested document.";
                    failures[item.SourceDocumentId] = new CreateFailure(item.SourceDocumentId, errorCode, error);
                    continue;
                }

                if (result.ErrorCode is not null)
                {
                    failures[item.SourceDocumentId] = new CreateFailure(
                        item.SourceDocumentId,
                        result.ErrorCode,
                        result.Error ?? "MoySklad rejected the purchasereturn.");
                    retry[item.SourceDocumentId] = item;
                    continue;
                }

                if (result.DocumentId is null || result.DocumentId == Guid.Empty)
                {
                    failures[item.SourceDocumentId] = new CreateFailure(
                        item.SourceDocumentId,
                        "PURCHASERETURN_CREATE_RESPONSE_INVALID",
                        "MoySklad did not return a valid new purchasereturn id.");
                    continue;
                }

                successfulIds.Add(item.SourceDocumentId);
                created.Add(new PurchaseReturnVerificationInput(
                    item.SourceDocumentId,
                    result.DocumentId.Value,
                    item.SyncId,
                    item.References.ContractId,
                    item.References.AgentAccountId,
                    result.RawJson));
                failures.Remove(item.SourceDocumentId);
                _logger.LogInformation(
                    "purchasereturn recreated: account_id={AccountId}, source_document_id={SourceDocumentId}, new_document_id={NewDocumentId}",
                    accountId,
                    item.SourceDocumentId,
                    result.DocumentId.Value);
            }

            foreach (var successfulId in successfulIds)
                pending.Remove(successfulId);
            pending = retry;
            if (pending.Count > 0 && attempt < _options.MaxAttempts)
                await DelayBeforeRetryAsync(attempt, cancellationToken);
        }

        foreach (var item in pending.Values)
        {
            if (!failures.ContainsKey(item.SourceDocumentId))
                failures[item.SourceDocumentId] = new CreateFailure(
                    item.SourceDocumentId,
                    "PURCHASERETURN_CREATE_RETRY_EXHAUSTED",
                    "purchasereturn create retry attempts exhausted.");
        }

        return (created, failures.Values.ToArray());
    }

    private async Task<(IReadOnlyList<Guid> Deleted, IReadOnlyDictionary<Guid, DeleteFailure> Failed)> DeleteWithRetryAsync(
        Guid accountId,
        IReadOnlyList<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var pending = purchaseReturnIds.ToArray();
        var deleted = new List<Guid>();
        var failures = new Dictionary<Guid, DeleteFailure>();
        for (var attempt = 1; pending.Length > 0 && attempt <= _options.MaxAttempts; attempt++)
        {
            IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult> results;
            try
            {
                results = await _gateway.DeleteBatchAsync(
                    accountId, Guid.NewGuid().ToString("D"), pending, cancellationToken);
            }
            catch (EgressException exception) when (exception.Retryable && attempt < _options.MaxAttempts)
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }
            catch (Exception exception) when (exception is EgressException or InvalidOperationException)
            {
                _logger.LogError(exception, "purchasereturn delete batch failed: account_id={AccountId}", accountId);
                foreach (var documentId in pending)
                    failures[documentId] = new DeleteFailure(
                        documentId,
                        exception is EgressException e ? e.Code : "PURCHASERETURN_DELETE_FAILED",
                        exception.Message);
                break;
            }

            var failed = new List<Guid>();
            foreach (var result in results)
            {
                if (result.Succeeded)
                    deleted.Add(result.DocumentId);
                else
                {
                    failures[result.DocumentId] = new DeleteFailure(
                        result.DocumentId,
                        result.ErrorCode ?? "PURCHASERETURN_DELETE_FAILED",
                        result.Error ?? "MoySklad rejected the purchasereturn deletion.");
                    failed.Add(result.DocumentId);
                }
            }

            pending = failed.ToArray();
            if (pending.Length > 0 && attempt < _options.MaxAttempts)
                await DelayBeforeRetryAsync(attempt, cancellationToken);
        }
        foreach (var documentId in pending)
        {
            if (!failures.ContainsKey(documentId))
                failures[documentId] = new DeleteFailure(
                    documentId,
                    "PURCHASERETURN_DELETE_RETRY_EXHAUSTED",
                    "purchasereturn delete retry attempts exhausted.");
        }
        return (deleted, failures);
    }

    private async Task<PurchaseReturnRecreationReferences> ResolveReferencesAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IEnumerable<string> documents,
        CancellationToken cancellationToken)
    {
        var rawDocuments = documents
            .Select(TryParseObject)
            .Where(document => document is not null)
            .Cast<JsonObject>()
            .ToArray();
        var needsContract = rawDocuments.Any(document => HasMeaningfulValue(document["contract"]));
        var needsAccount = rawDocuments.Any(document => HasMeaningfulValue(document["agentAccount"]));
        Guid? contractId = null;
        Guid? accountReferenceId = null;
        var correlationId = Guid.NewGuid().ToString("D");

        if (needsContract)
        {
            var contracts = await _gateway.GetContractsAsync(
                accountId, mainCounterpartyId, correlationId, cancellationToken);
            contractId = contracts.FirstOrDefault(item => item.IsDefault)?.Id;
        }
        if (needsAccount)
        {
            var accounts = await _gateway.GetAgentAccountsAsync(
                accountId, mainCounterpartyId, correlationId, cancellationToken);
            accountReferenceId = accounts.FirstOrDefault(item => item.IsDefault)?.Id;
        }
        return new PurchaseReturnRecreationReferences(contractId, accountReferenceId);
    }

    private static PurchaseReturnRecreationReferences ReferencesFor(
        string rawDocument,
        PurchaseReturnRecreationReferences resolved)
    {
        var document = ParseObject(rawDocument);
        return new PurchaseReturnRecreationReferences(
            HasMeaningfulValue(document["contract"]) ? resolved.ContractId : null,
            HasMeaningfulValue(document["agentAccount"]) ? resolved.AgentAccountId : null);
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var multiplier = Math.Pow(2, attempt - 1);
        var delay = TimeSpan.FromMilliseconds(_options.InitialRetryDelay.TotalMilliseconds * multiplier);
        await Task.Delay(delay, cancellationToken);
    }

    private void LogSkipped(PurchaseReturnPreparationResult preparation, Guid accountId)
    {
        foreach (var skipped in preparation.Skipped)
            _logger.LogWarning(
                "purchasereturn skipped: account_id={AccountId}, purchasereturn_id={PurchaseReturnId}, reason={Reason}",
                accountId,
                skipped.PurchaseReturnId,
                skipped.Reason);
    }

    private static JsonObject ParseObject(string rawJson) =>
        JsonNode.Parse(rawJson)?.AsObject()
        ?? throw new InvalidOperationException("Saved purchasereturn data is not a JSON object.");

    private static JsonObject? TryParseObject(string rawJson)
    {
        try
        {
            return ParseObject(rawJson);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool HasMeaningfulValue(JsonNode? node)
    {
        if (node is null)
            return false;
        return node is not JsonValue value || value.ToJsonString() != "null";
    }

    private static PurchaseReturnDocumentVerificationResult Failed(
        Guid sourceId,
        string status,
        string code,
        string message) => new(sourceId, null, status, [], [], [], code, message);

    private sealed record PreparedPurchaseReturn(
        Guid SourceDocumentId,
        Guid SyncId,
        PurchaseReturnRecreationReferences References,
        string PayloadJson);

    private sealed record CreateFailure(Guid SourceDocumentId, string ErrorCode, string Error);

    private sealed record DeleteFailure(Guid SourceDocumentId, string ErrorCode, string Error);
}
