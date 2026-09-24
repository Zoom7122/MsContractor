using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.DuplicatesMergeService.Services.Merge;

public interface IMergeOperationStateService
{
    Task EnsureDocumentChangeOperationAsync(MergeJob job, CancellationToken cancellationToken);
    Task EnsureSalesReturnRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken);
    Task EnsurePurchaseReturnRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken);
    Task EnsureFactureInRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken);
    Task StartJobAsync(MergeJob job, CancellationToken cancellationToken);
    Task<bool> ExecuteAsync(MergeJob job, MergeOperation operation, Func<CancellationToken, Task> action,
        string retryMessage, CancellationToken cancellationToken);
    Task ExecuteBatchAsync(MergeJob job, IReadOnlyList<MergeOperation> operations, Func<CancellationToken, Task> action,
        string retryMessage, CancellationToken cancellationToken);
    Task CompleteJobAsync(MergeJob job, CancellationToken cancellationToken);
}

public sealed class MergeOperationStateService(
    IMergeRepository repository,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<MergeOperationStateService> logger) : IMergeOperationStateService
{
    private const string ConsumerName = "duplicates-merge-v1";
    private int MaxAttempts => Math.Max(1, configuration.GetValue("Merge:MaxOperationAttempts", 5));

    public async Task EnsureDocumentChangeOperationAsync(MergeJob job, CancellationToken cancellationToken)
    {
        if (job.Operations.Any(item => item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties))
            return;

        var now = timeProvider.GetUtcNow();
        var operation = new MergeOperation
        {
            Id = Guid.NewGuid(), MergeJobId = job.Id, MergeJob = job, AccountId = job.AccountId,
            Sequence = 2, OperationType = MergeOperationTypes.ChangeDocumentCounterparties,
            CounterpartyId = job.MainCounterpartyId, Status = MergeOperationStatuses.Pending,
            CreatedAt = now, UpdatedAt = now
        };
        await repository.InsertOperationAsync(job.AccountId, job, operation, cancellationToken);
    }

    public async Task EnsureSalesReturnRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken)
    {
        if (job.Operations.Any(item => item.OperationType == MergeOperationTypes.RecreateSalesReturns))
            return;

        var now = timeProvider.GetUtcNow();
        var operation = new MergeOperation
        {
            Id = Guid.NewGuid(), MergeJobId = job.Id, MergeJob = job, AccountId = job.AccountId,
            Sequence = job.Operations.Any() ? job.Operations.Max(item => item.Sequence) + 1 : 0,
            OperationType = MergeOperationTypes.RecreateSalesReturns,
            CounterpartyId = job.MainCounterpartyId, Status = MergeOperationStatuses.Pending,
            CreatedAt = now, UpdatedAt = now
        };
        await repository.InsertOperationAsync(job.AccountId, job, operation, cancellationToken);
    }

    public async Task EnsurePurchaseReturnRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken)
    {
        if (job.Operations.Any(item => item.OperationType == MergeOperationTypes.RecreatePurchaseReturns))
            return;

        var salesReturn = job.Operations
            .SingleOrDefault(item => item.OperationType == MergeOperationTypes.RecreateSalesReturns);
        var now = timeProvider.GetUtcNow();
        var operation = new MergeOperation
        {
            Id = Guid.NewGuid(), MergeJobId = job.Id, MergeJob = job, AccountId = job.AccountId,
            Sequence = salesReturn is null ? job.Operations.Count : salesReturn.Sequence + 1,
            OperationType = MergeOperationTypes.RecreatePurchaseReturns,
            CounterpartyId = job.MainCounterpartyId, Status = MergeOperationStatuses.Pending,
            CreatedAt = now, UpdatedAt = now
        };
        await repository.InsertOperationAsync(job.AccountId, job, operation, cancellationToken);
    }

    public async Task EnsureFactureInRecreationOperationAsync(MergeJob job, CancellationToken cancellationToken)
    {
        if (job.Operations.Any(item => item.OperationType == MergeOperationTypes.RecreateFactureIns))
            return;

        var purchaseReturn = job.Operations
            .SingleOrDefault(item => item.OperationType == MergeOperationTypes.RecreatePurchaseReturns);
        var sequence = purchaseReturn is null
            ? job.Operations.Where(item => item.OperationType != MergeOperationTypes.ArchiveDuplicate)
                .DefaultIfEmpty().Max(item => item?.Sequence ?? -1) + 1
            : purchaseReturn.Sequence + 1;
        var now = timeProvider.GetUtcNow();
        var operation = new MergeOperation
        {
            Id = Guid.NewGuid(), MergeJobId = job.Id, MergeJob = job, AccountId = job.AccountId,
            Sequence = sequence, OperationType = MergeOperationTypes.RecreateFactureIns,
            CounterpartyId = job.MainCounterpartyId, Status = MergeOperationStatuses.Pending,
            CreatedAt = now, UpdatedAt = now
        };
        foreach (var archive in job.Operations
                     .Where(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate && item.Sequence <= sequence)
                     .OrderBy(item => item.Sequence).ThenBy(item => item.Id).Select((item, index) => (item, index)))
            archive.item.Sequence = sequence + archive.index + 1;

        await repository.InsertOperationAsync(job.AccountId, job, operation, cancellationToken);
    }

    public async Task StartJobAsync(MergeJob job, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        job.Status = MergeJobStatuses.Running;
        job.StartedAt ??= now;
        job.UpdatedAt = now;
        await repository.SaveProgressAsync(job.AccountId, cancellationToken);
    }

    public async Task<bool> ExecuteAsync(MergeJob job, MergeOperation operation, Func<CancellationToken, Task> action,
        string retryMessage, CancellationToken cancellationToken)
    {
        if (MergeOperationStatuses.IsTerminal(operation.Status))
        {
            if (operation.Status == MergeOperationStatuses.Failed)
            {
                await FailJobAsync(job, cancellationToken);
                return false;
            }
            return true;
        }

        await MarkRunningAsync(operation, cancellationToken);
        try
        {
            await action(cancellationToken);
            Complete(operation);
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            if (await HandleFailureAsync(operation, exception, cancellationToken))
                throw new MergeRetryableException(retryMessage, exception);

            await FailJobAsync(job, cancellationToken);
            return false;
        }
    }

    public async Task ExecuteBatchAsync(MergeJob job, IReadOnlyList<MergeOperation> operations, Func<CancellationToken, Task> action,
        string retryMessage, CancellationToken cancellationToken)
    {
        foreach (var operation in operations)
            await MarkRunningAsync(operation, cancellationToken);

        try
        {
            await action(cancellationToken);
            foreach (var operation in operations)
                Complete(operation);
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            var shouldRetry = false;
            foreach (var operation in operations)
                shouldRetry |= await HandleFailureAsync(operation, exception, cancellationToken);
            if (shouldRetry)
                throw new MergeRetryableException(retryMessage, exception);
        }
    }

    public async Task CompleteJobAsync(MergeJob job, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        job.Status = job.Operations.Any(item => item.Status == MergeOperationStatuses.Failed)
            ? MergeJobStatuses.PartiallyCompleted
            : MergeJobStatuses.Completed;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        await repository.SaveTerminalAsync(job.AccountId, job, NewInbox(job.MessageId, now), cancellationToken);
    }

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

    private async Task<bool> HandleFailureAsync(MergeOperation operation, Exception exception, CancellationToken cancellationToken)
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
            logger.LogWarning("Merge operation will be retried: operation_id={OperationId}, operation_type={OperationType}, counterparty_id={CounterpartyId}, status={Status}, error_code={ErrorCode}, error_message={ErrorMessage}",
                operation.Id, operation.OperationType, operation.CounterpartyId, operation.Status, operation.ErrorCode, operation.ErrorMessage);
            return true;
        }

        operation.Status = MergeOperationStatuses.Failed;
        operation.ErrorCode = SafeCode(exception);
        operation.ErrorMessage = retryable
            ? LimitErrorMessage($"The Egress retry policy was exhausted. {SafeMessage(exception)}")
            : SafeMessage(exception);
        operation.CompletedAt = operation.UpdatedAt;
        await repository.SaveProgressAsync(operation.AccountId, cancellationToken);
        logger.LogError(exception, "Merge operation failed: operation_id={OperationId}, operation_type={OperationType}, counterparty_id={CounterpartyId}, status={Status}, error_code={ErrorCode}, error_message={ErrorMessage}",
            operation.Id, operation.OperationType, operation.CounterpartyId, operation.Status, operation.ErrorCode, operation.ErrorMessage);
        return false;
    }

    private async Task FailJobAsync(MergeJob job, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        job.Status = MergeJobStatuses.Failed;
        job.CompletedAt = now;
        job.UpdatedAt = now;
        await repository.SaveTerminalAsync(job.AccountId, job, NewInbox(job.MessageId, now), cancellationToken);
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

    private static InboxMessage NewInbox(Guid messageId, DateTimeOffset now) => new()
    {
        MessageId = messageId, ConsumerName = ConsumerName, ProcessedAt = now
    };
    private static bool IsOperationFailure(Exception exception) => exception is MergeEgressException or JsonException;
    private static bool IsRetryable(Exception exception) => exception is JsonException || exception is MergeEgressException { IsRetryable: true };
    private static string SafeCode(Exception exception) => exception is MergeEgressException egress ? egress.Code : "EGRESS_INVALID_RESPONSE";
    private static string SafeMessage(Exception exception) => LimitErrorMessage(exception is MergeEgressException egress
        ? egress.SafeMessage : "Egress returned invalid counterparty data.");
    private static string LimitErrorMessage(string message) => message.Length <= 512 ? message : message[..512];
}
