using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed class MergeCommandRejectedException(string message) : Exception(message);
public sealed class MergeRetryableException(string message, Exception innerException) : Exception(message, innerException);

public interface IMergeProcessor
{
    Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken);
}

public sealed partial class MergeProcessor(
    CatalogSyncDbContext dbContext,
    IMergeEgressClient egressClient,
    IDocumentDiscoveryEgressClient documentDiscoveryClient,
    IMoySkladCounterpartyParser parser,
    ICounterpartyNormalizer normalizer,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<MergeProcessor> logger) : IMergeProcessor
{
    public const string ConsumerName = "duplicates-merge-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int MaxAttempts => Math.Max(1, configuration.GetValue("Merge:MaxOperationAttempts", 5));

    public async Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(command.AccountId, cancellationToken);

        if (await dbContext.InboxMessages.AnyAsync(
                item => item.MessageId == command.MessageId && item.ConsumerName == ConsumerName,
                cancellationToken))
            return;

        var job = await dbContext.MergeJobs
            .Include(item => item.Operations)
            .SingleOrDefaultAsync(
                item => item.Id == command.MergeJobId && item.AccountId == command.AccountId,
                cancellationToken)
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
            await AddInboxAsync(job.MessageId, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        job.Status = MergeJobStatuses.Running;
        job.StartedAt ??= now;
        job.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

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
        AddInbox(job.MessageId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

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
            await dbContext.SaveChangesAsync(cancellationToken);
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

    private async Task ReplaceDocumentSnapshotAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> counterpartyIds,
        IReadOnlyList<MoySkladDocumentReference> documents,
        CancellationToken cancellationToken)
    {
        var knownCounterpartyIds = counterpartyIds.ToHashSet();
        var uniqueDocumentKeys = new HashSet<(string DocumentType, Guid DocumentId)>();
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentType) || document.DocumentId == Guid.Empty ||
                document.CounterpartyId == Guid.Empty || !knownCounterpartyIds.Contains(document.CounterpartyId) ||
                !uniqueDocumentKeys.Add((document.DocumentType, document.DocumentId)))
            {
                throw new JsonException("MoySklad document discovery response is inconsistent.");
            }
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.CounterpartyDocuments
            .Where(item => item.AccountId == accountId && knownCounterpartyIds.Contains(item.CounterpartyId))
            .ExecuteDeleteAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        dbContext.CounterpartyDocuments.AddRange(documents.Select(document => new CounterpartyDocument
        {
            AccountId = accountId,
            CounterpartyId = document.CounterpartyId,
            DocumentType = document.DocumentType,
            DocumentId = document.DocumentId,
            UpdatedAt = now
        }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

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
            await dbContext.SaveChangesAsync(cancellationToken);
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

    private async Task MarkRunningAsync(MergeOperation operation, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        operation.Status = MergeOperationStatuses.Running;
        operation.StartedAt ??= now;
        operation.UpdatedAt = now;
        operation.ErrorCode = null;
        operation.ErrorMessage = null;
        await dbContext.SaveChangesAsync(cancellationToken);
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
            await dbContext.SaveChangesAsync(cancellationToken);
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
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
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
        AddInbox(job.MessageId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Counterparty> FindLocalAsync(
        Guid accountId,
        Guid counterpartyId,
        CancellationToken cancellationToken) =>
        await dbContext.Counterparties.SingleOrDefaultAsync(
            item => item.AccountId == accountId && item.Id == counterpartyId,
            cancellationToken)
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

    private async Task AddInboxAsync(Guid messageId, CancellationToken cancellationToken)
    {
        AddInbox(messageId, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private void AddInbox(Guid messageId, DateTimeOffset now)
    {
        if (!dbContext.InboxMessages.Local.Any(item => item.MessageId == messageId && item.ConsumerName == ConsumerName))
        {
            dbContext.InboxMessages.Add(new InboxMessage
            {
                MessageId = messageId,
                ConsumerName = ConsumerName,
                ProcessedAt = now
            });
        }
    }

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
