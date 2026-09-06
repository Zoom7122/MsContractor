using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.CatalogSyncService.Models;
using MsContractor.DuplicatesMergeService.Clients;
using System.Text.Json;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public interface IMergeProcessor
{
    /// <summary>
    /// Запуск merge операции
    /// </summary>
    /// <param name="command"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken);
}

public sealed partial class MergeProcessor(
    IMergeRepository repository,
    ICounterpartyRepository counterparties,
    IDocumentSnapshotRepository documentSnapshots,
    IMergeEgressClient egressClient,
    IDocumentDiscoveryEgressClient documentDiscoveryClient,
    IDocumentChangeEgressClient documentChangeClient,
    IMoySkladCounterpartyParser parser,
    ICounterpartyNormalizer normalizer,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<MergeProcessor> logger) : IMergeProcessor
{
    public const string ConsumerName = "duplicates-merge-v1";
    private const int CommissionReportBatchSize = 950;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int MaxAttempts => Math.Max(1, configuration.GetValue("Merge:MaxOperationAttempts", 5));

    public async Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken)
    {
        #region Validation
        //чтобы операции были scoped по id



        if (await repository.HasProcessedAsync(command.AccountId, command.MessageId, ConsumerName, cancellationToken))
            return;

        //Загрузка задачи
        var job = await repository.FindJobAsync(command.AccountId, command.MergeJobId, cancellationToken)
            ?? throw new MergeCommandRejectedException("Merge job was not found.");

        var snapshot = DeserializeAndVerify(job, command);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["account_id"] = job.AccountId,
            ["merge_job_id"] = job.Id,
            ["message_id"] = job.MessageId,
            ["correlation_id"] = job.CorrelationId
        });

        if (MergeJobStatuses.IsTerminal(job.Status))
        {
            await repository.SaveInboxAsync(job.AccountId, NewInbox(job.MessageId, timeProvider.GetUtcNow()), cancellationToken);
            return;
        }

        await EnsureDocumentChangeOperationAsync(job, cancellationToken);

        #endregion Validation


        //комит задачи ранинг
        var now = timeProvider.GetUtcNow();
        job.Status = MergeJobStatuses.Running;
        job.StartedAt ??= now;
        job.UpdatedAt = now;
        await repository.SaveProgressAsync(job.AccountId, cancellationToken);


        //есть ли запись об операции 
        var discovery = job.Operations.Single(item => item.OperationType == MergeOperationTypes.DiscoverDocuments);
        if (!MergeOperationStatuses.IsTerminal(discovery.Status))
        {
            if (!await ExecuteDocumentDiscoveryAsync(job, discovery, command.DuplicateCounterpartyIds, cancellationToken))
                return;
        }
        else if (discovery.Status == MergeOperationStatuses.Failed)
        {
            await FailJobAsync(job, cancellationToken);
            return;
        }

        var update = job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty);
        if (!MergeOperationStatuses.IsTerminal(update.Status))
        {
            if (!await ExecuteUpdateMainAsync(job, update, snapshot, cancellationToken))
                return;
        }
        else if (update.Status == MergeOperationStatuses.Failed)
        {
            await FailJobAsync(job, cancellationToken);
            return;
        }

        var documentChange = job.Operations.Single(
            item => item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties);
        if (!MergeOperationStatuses.IsTerminal(documentChange.Status))
        {
            if (!await ExecuteDocumentChangeAsync(
                    job, documentChange, command.DuplicateCounterpartyIds, cancellationToken))
                return;
        }
        else if (documentChange.Status == MergeOperationStatuses.Failed)
        {
            await FailJobAsync(job, cancellationToken);
            return;
        }

        var archiveOperations = job.Operations
            .Where(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate)
            .Where(item => !MergeOperationStatuses.IsTerminal(item.Status))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (archiveOperations.Length > 0)
            await ExecuteArchiveBatchAsync(job, archiveOperations, cancellationToken);

        now = timeProvider.GetUtcNow();
        job.Status = job.Operations.Any(item => item.Status == MergeOperationStatuses.Failed)
            ? MergeJobStatuses.PartiallyCompleted
            : MergeJobStatuses.Completed;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        await repository.SaveInboxAsync(job.AccountId, NewInbox(job.MessageId, now), cancellationToken);
    }

    /// <summary>
    /// добавляет в таблицу merge_operations одну служебную запись о смене документов
    /// </summary>
    /// <param name="job"> Задача </param>
    /// <param name="cancellationToken"> токен отмены </param>
    /// <returns></returns>
    private async Task EnsureDocumentChangeOperationAsync(MergeJob job, CancellationToken cancellationToken)
    {
        if (job.Operations.Any(item => item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties))
            return;

        var now = timeProvider.GetUtcNow();
        var documentChange = new MergeOperation
        {
            Id = Guid.NewGuid(),
            MergeJobId = job.Id,
            MergeJob = job,
            AccountId = job.AccountId,
            Sequence = 2,
            OperationType = MergeOperationTypes.ChangeDocumentCounterparties,
            CounterpartyId = job.MainCounterpartyId,
            Status = MergeOperationStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        await repository.InsertOperationAsync(job.AccountId, job, documentChange, cancellationToken);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="job"></param>
    /// <param name="operation"></param>
    /// <param name="duplicateCounterpartyIds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="MergeRetryableException"></exception>
    private async Task<bool> ExecuteDocumentChangeAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(operation, cancellationToken);
        try
        {
            var rows = await documentSnapshots.GetForCounterpartiesAsync(job.AccountId, duplicateCounterpartyIds, cancellationToken);
            if (rows.Count == 0)
            {
                Complete(operation);
                await repository.SaveProgressAsync(job.AccountId, cancellationToken);
                return true;
            }

            var commissionRows = rows.Where(item => IsCommissionReport(item.DocumentType)).ToArray();
            if (commissionRows.Length > 0)
            {
                var commissionIds = commissionRows.Select(item => item.DocumentId).ToArray();
                var contracts = await documentSnapshots.GetCommissionsAsync(job.AccountId, commissionIds, cancellationToken);

                foreach (var chunk in commissionRows.Chunk(CommissionReportBatchSize))
                {
                    var documents = chunk.Select(item => new MoySkladDocumentChangeAgentAndContractItem(
                        item.DocumentType,
                        item.DocumentId,
                        contracts.GetValueOrDefault(item.DocumentId))).ToArray();
                    var response = await documentChangeClient.ChangeAgentAndContractAsync(
                        job.AccountId,
                        job.MainCounterpartyId,
                        documents,
                        job.Id,
                        operation.Id,
                        job.RequestedByUserId,
                        job.CorrelationId,
                        cancellationToken);
                    var requested = documents.Select(item => new MoySkladDocumentChangeItem(
                        item.DocumentType, item.DocumentId)).ToArray();
                    ValidateDocumentChangeResponse(job.MainCounterpartyId, requested, response);
                    await ApplyChangedDocumentsAsync(chunk, response, job.MainCounterpartyId, cancellationToken);
                    ThrowIfDocumentChangeFailed(response);
                }
            }

            var ordinaryRows = rows.Where(item => !IsCommissionReport(item.DocumentType)).ToArray();
            if (ordinaryRows.Length > 0)
            {
                var documents = ordinaryRows
                    .Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId))
                    .ToArray();
                var response = await documentChangeClient.ChangeCounterpartyAsync(
                    job.AccountId,
                    job.MainCounterpartyId,
                    documents,
                    job.Id,
                    operation.Id,
                    job.RequestedByUserId,
                    job.CorrelationId,
                    cancellationToken);
                ValidateDocumentChangeResponse(job.MainCounterpartyId, documents, response);
                await ApplyChangedDocumentsAsync(ordinaryRows, response, job.MainCounterpartyId, cancellationToken);
                ThrowIfDocumentChangeFailed(response);
            }

            Complete(operation);
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            if (await HandleFailureAsync(operation, exception, cancellationToken))
                throw new MergeRetryableException("Document counterparty change will be retried.", exception);

            await FailJobAsync(job, cancellationToken);
            return false;
        }
    }


/// <summary>
/// обновляет в нашей БД принадлежность документов, которые Egress успешно перенёс в МойСклад
/// </summary>
/// <param name="rows"></param>
/// <param name="response"></param>
/// <param name="mainCounterpartyId"></param>
/// <param name="cancellationToken"></param>
/// <returns></returns>
    private async Task ApplyChangedDocumentsAsync(
        IReadOnlyList<CounterpartyDocument> rows,
        MoySkladDocumentChangeCounterpartyResponse response,
        Guid mainCounterpartyId,
        CancellationToken cancellationToken)
    {
        var changedKeys = response.ChangedDocuments
            .Select(item => (item.DocumentType, item.DocumentId))
            .ToHashSet();
        var now = timeProvider.GetUtcNow();
        foreach (var row in rows.Where(item => changedKeys.Contains((item.DocumentType, item.DocumentId))))
        {
            row.CounterpartyId = mainCounterpartyId;
            row.UpdatedAt = now;
        }
        await documentSnapshots.SaveProgressAsync(rows[0].AccountId, cancellationToken);
    }

    private static void ThrowIfDocumentChangeFailed(MoySkladDocumentChangeCounterpartyResponse response)
    {
        if (response.Failures.Count == 0)
            return;

        var allRetryable = response.Failures.All(item => item.Retryable);
        var representative = response.Failures.FirstOrDefault(item => !item.Retryable) ?? response.Failures[0];
        throw new MergeEgressException(
            representative.Code,
            $"Document counterparty change failed for {response.Failures.Count} document(s). " +
            $"document_type={representative.DocumentType}, document_id={representative.DocumentId:D}, " +
            $"endpoint={representative.Endpoint ?? "unknown"}, http_status={representative.StatusCode}, " +
            $"retryable={representative.Retryable}. {representative.Message}",
            allRetryable ? 503 : 400);
    }

    private static void ValidateDocumentChangeResponse(
        Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> requested,
        MoySkladDocumentChangeCounterpartyResponse response)
    {
        var requestedKeys = requested.Select(item => (item.DocumentType, item.DocumentId)).ToHashSet();
        var returnedKeys = response.ChangedDocuments.Select(item => (item.DocumentType, item.DocumentId))
            .Concat(response.SkippedDocuments.Select(item => (item.DocumentType, item.DocumentId)))
            .Concat(response.Failures.Select(item => (item.DocumentType, item.DocumentId)))
            .ToArray();
        if (response.MainCounterpartyId != mainCounterpartyId ||
            response.RequestedCount != requested.Count ||
            response.ChangedCount != response.ChangedDocuments.Count ||
            response.SkippedCount != response.SkippedDocuments.Count ||
            response.FailedCount != response.Failures.Count ||
            returnedKeys.Length != requested.Count ||
            returnedKeys.Distinct().Count() != returnedKeys.Length ||
            !returnedKeys.ToHashSet().SetEquals(requestedKeys))
        {
            throw new JsonException("Egress returned an inconsistent document change response.");
        }
    }

    /// <summary>
    /// запрашивает документы из Egress для основного КА и всех дублей
    /// </summary>
    /// <param name="job"></param>
    /// <param name="operation"></param>
    /// <param name="duplicateCounterpartyIds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="MergeRetryableException"></exception>
    private async Task<bool> ExecuteDocumentDiscoveryAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(operation, cancellationToken);
        try
        {
            var counterpartyIds = duplicateCounterpartyIds.Append(job.MainCounterpartyId).ToArray();

            var response = await documentDiscoveryClient.DiscoverAsync(
                job.AccountId,
                counterpartyIds,
                job.Id,
                operation.Id,
                job.RequestedByUserId,
                job.CorrelationId,
                cancellationToken);

            await ReplaceDocumentSnapshotAsync(job.AccountId, counterpartyIds, response.Documents, cancellationToken);
            Complete(operation);
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
            logger.LogInformation(
                "MoySklad document discovery completed: operation_id={OperationId}, documents_count={DocumentsCount}",
                operation.Id,
                response.Documents.Count);
            return true;
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            if (await HandleFailureAsync(operation, exception, cancellationToken))
                throw new MergeRetryableException("Document discovery will be retried.", exception);

            await FailJobAsync(job, cancellationToken);
            return false;
        }
    }

    /// <summary>
    /// Обновление локального снимка документов перед их переносом в МС
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="counterpartyIds"></param>
    /// <param name="documents"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="JsonException"></exception>
    private async Task ReplaceDocumentSnapshotAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> counterpartyIds,
        IReadOnlyList<MoySkladDocumentReference> documents,
        CancellationToken cancellationToken)
    {
        var knownCounterpartyIds = counterpartyIds.ToHashSet();
        var uniqueDocumentKeys = new HashSet<(string DocumentType, Guid DocumentId)>();
        var uniqueDocumentIds = new HashSet<Guid>();
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentType) || document.DocumentId == Guid.Empty ||
                document.CounterpartyId == Guid.Empty || !knownCounterpartyIds.Contains(document.CounterpartyId) ||
                !uniqueDocumentKeys.Add((document.DocumentType, document.DocumentId)) ||
                !uniqueDocumentIds.Add(document.DocumentId))
            {
                throw new JsonException("MoySklad document discovery response is inconsistent.");
            }
        }

        var now = timeProvider.GetUtcNow();

        //все документы поолучееные
        var rows = documents.Select(document => new CounterpartyDocument
        {
            AccountId = accountId,
            CounterpartyId = document.CounterpartyId,
            DocumentType = document.DocumentType,
            DocumentId = document.DocumentId,
            UpdatedAt = now
        }).ToArray();

        //Документы типа commissions (commissionreportin и commissionreportout)
        var commissions = documents
            .Where(document => IsCommissionReport(document.DocumentType))
            .Select(document => new DocumentAdditionalCommission
            {
                DocumentId = document.DocumentId,
                Contract = document.ContractId
            }).ToArray();

        //salesreturn, purchasereturn, retailsalesreturn, factureout, facturein, если есть непустой JSON
        var additionalData = documents
            .Where(document => IsRawAdditionalDataDocument(document.DocumentType) &&
                               !string.IsNullOrWhiteSpace(document.RawJson))
            .Select(document => new DocumentAdditionalData
            {
                DocumentId = document.DocumentId,
                RawJson = document.RawJson!
            }).ToArray();
        await documentSnapshots.ReplaceAsync(accountId, counterpartyIds, rows, commissions, additionalData, cancellationToken);
    }

    private static bool IsCommissionReport(string documentType) =>
        string.Equals(documentType, "commissionreportin", StringComparison.Ordinal) ||
        string.Equals(documentType, "commissionreportout", StringComparison.Ordinal);

    private static bool IsRawAdditionalDataDocument(string documentType) =>
        documentType is "salesreturn" or "purchasereturn" or "retailsalesreturn" or "factureout" or "facturein";

    private async Task ExecuteArchiveBatchAsync(
        MergeJob job,
        IReadOnlyList<MergeOperation> operations,
        CancellationToken cancellationToken)
    {
        foreach (var operation in operations)
            await MarkRunningAsync(operation, cancellationToken);

        try
        {
            // Local Archived=true deliberately does not skip this external operation.
            var response = await egressClient.ArchiveAsync(
                job.AccountId, operations.Select(item => item.CounterpartyId).ToArray(),
                job.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
            var parsedById = ParseArchiveBatch(response.Json, operations);
            foreach (var operation in operations)
            {
                var parsed = parsedById[operation.CounterpartyId];
                var local = await FindLocalAsync(job.AccountId, operation.CounterpartyId, cancellationToken);
                normalizer.Apply(local, parsed, timeProvider.GetUtcNow());
                Complete(operation);
            }
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            var shouldRetry = false;
            foreach (var operation in operations)
                shouldRetry |= await HandleFailureAsync(operation, exception, cancellationToken);
            if (shouldRetry)
                throw new MergeRetryableException("Archive counterparties will be retried.", exception);
        }
    }

    private IReadOnlyDictionary<Guid, MsContractor.CatalogSyncService.Models.ParsedCounterparty> ParseArchiveBatch(
        string json,
        IReadOnlyList<MergeOperation> operations)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("MoySklad batch archive response must be an array.");

        var expectedIds = operations.Select(item => item.CounterpartyId).ToHashSet();
        var parsedById = new Dictionary<Guid, MsContractor.CatalogSyncService.Models.ParsedCounterparty>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var parsed = parser.ParseOne(item.GetRawText());
            EnsureResponse(parsed.Value.Id, parsed, archivedRequired: true);
            if (!expectedIds.Contains(parsed.Value.Id) || !parsedById.TryAdd(parsed.Value.Id, parsed))
                throw new JsonException("MoySklad batch archive response does not match requested counterparties.");
        }

        if (parsedById.Count != expectedIds.Count)
            throw new JsonException("MoySklad batch archive response is incomplete.");
        return parsedById;
    }


    /// <summary>
    /// маркирует что делается операция 
    /// </summary>
    /// <param name="operation"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task MarkRunningAsync(MergeOperation operation, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        operation.Status = MergeOperationStatuses.Running;
        operation.StartedAt ??= now;
        operation.UpdatedAt = now;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        await repository.SaveProgressAsync(operation.AccountId, cancellationToken);
    }

    private async Task<bool> HandleFailureAsync(
        MergeOperation operation,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var retryable = IsRetryable(exception);
        operation.AttemptCount++;
        operation.UpdatedAt = timeProvider.GetUtcNow();
        if (retryable && operation.AttemptCount < MaxAttempts)
        {
            operation.Status = MergeOperationStatuses.Pending;
            operation.ErrorCode = SafeCode(exception);
            operation.ErrorMessage = SafeMessage(exception);
            await repository.SaveProgressAsync(operation.AccountId, cancellationToken);
            logger.LogWarning(
                "Merge operation will be retried: operation_id={OperationId}, operation_type={OperationType}, counterparty_id={CounterpartyId}, status={Status}, error_code={ErrorCode}, error_message={ErrorMessage}",
                operation.Id, operation.OperationType, operation.CounterpartyId, operation.Status, operation.ErrorCode, operation.ErrorMessage);
            return true;
        }

        operation.Status = MergeOperationStatuses.Failed;
        // Keep the upstream code even when attempts are exhausted: it is the actionable
        // cause for operators, while the message records that no retry remains.
        operation.ErrorCode = SafeCode(exception);
        operation.ErrorMessage = retryable
            ? LimitErrorMessage($"The Egress retry policy was exhausted. {SafeMessage(exception)}")
            : SafeMessage(exception);
        operation.CompletedAt = operation.UpdatedAt;
        await repository.SaveProgressAsync(operation.AccountId, cancellationToken);
        logger.LogError(
            exception,
            "Merge operation failed: operation_id={OperationId}, operation_type={OperationType}, counterparty_id={CounterpartyId}, status={Status}, error_code={ErrorCode}, error_message={ErrorMessage}",
            operation.Id, operation.OperationType, operation.CounterpartyId, operation.Status, operation.ErrorCode, operation.ErrorMessage);
        return false;
    }

    private async Task FailJobAsync(MergeJob job, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        job.Status = MergeJobStatuses.Failed;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        await repository.SaveInboxAsync(job.AccountId, NewInbox(job.MessageId, now), cancellationToken);
    }

    private async Task<Counterparty> FindLocalAsync(
        Guid accountId,
        Guid counterpartyId,
        CancellationToken cancellationToken) =>
        await counterparties.FindTrackedAsync(accountId, counterpartyId, cancellationToken)
        ?? throw new MergeEgressException("LOCAL_COUNTERPARTY_NOT_FOUND", "Local counterparty was not found.", 500);

    private static void EnsureResponse(
        Guid expectedId,
        MsContractor.CatalogSyncService.Models.ParsedCounterparty parsed,
        bool archivedRequired)
    {
        if (parsed.Value.Id != expectedId || (archivedRequired && !parsed.Value.Archived))
            throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "Egress returned an inconsistent counterparty.", 502);
    }

    private void Complete(MergeOperation operation)
    {
        var now = timeProvider.GetUtcNow();
        operation.Status = MergeOperationStatuses.Completed;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        operation.CompletedAt = now;
        operation.UpdatedAt = now;
    }

    private MergeMainCounterpartyDto DeserializeAndVerify(MergeJob job, MergeRequested command)
    {
        MergeMainCounterpartyDto snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<MergeMainCounterpartyDto>(job.Payload, JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException exception)
        {
            throw new MergeCommandRejectedException($"Stored merge payload is invalid: {exception.GetType().Name}.");
        }

        var archiveIds = job.Operations
            .Where(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate)
            .OrderBy(item => item.Sequence)
            .Select(item => item.CounterpartyId);
        if (job.PayloadVersion != command.SchemaVersion ||
            job.MessageId != command.MessageId ||
            job.CorrelationId != command.CorrelationId ||
            job.MainCounterpartyId != command.MainCounterpartyId ||
            job.RequestedByUserId != command.RequestedByUserId ||
            snapshot != command.MainCounterparty ||
            !archiveIds.SequenceEqual(command.DuplicateCounterpartyIds))
        {
            throw new MergeCommandRejectedException("Merge command does not match its durable job.");
        }
        return snapshot;
    }

    private static InboxMessage NewInbox(Guid messageId, DateTimeOffset now) => new()
    {
        MessageId = messageId,
        ConsumerName = ConsumerName,
        ProcessedAt = now
    };

    private static bool IsOperationFailure(Exception exception) =>
        exception is MergeEgressException or JsonException;

    private static bool IsRetryable(Exception exception) =>
        exception is JsonException || exception is MergeEgressException { IsRetryable: true };

    private static string SafeCode(Exception exception) =>
        exception is MergeEgressException egress ? egress.Code : "EGRESS_INVALID_RESPONSE";

    private static string SafeMessage(Exception exception) => LimitErrorMessage(
        exception is MergeEgressException egress ? egress.SafeMessage : "Egress returned invalid counterparty data.");

    private static string LimitErrorMessage(string message) =>
        message.Length <= 512 ? message : message[..512];
}
