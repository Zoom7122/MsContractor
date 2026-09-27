using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.DuplicatesMergeService.Services.Merge;

public sealed record MergeExecutionContext(MergeJob Job, MergeMainCounterpartyDto MainCounterparty);

public interface IMergeCommandValidator
{
    Task<MergeExecutionContext?> ValidateAsync(MergeRequested command, CancellationToken cancellationToken);
}

public sealed class MergeCommandValidator(
    IMergeRepository repository,
    TimeProvider timeProvider) : IMergeCommandValidator
{
    private const string ConsumerName = "duplicates-merge-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MergeExecutionContext?> ValidateAsync(MergeRequested command, CancellationToken cancellationToken)
    {
        if (await repository.HasProcessedAsync(command.AccountId, command.MessageId, ConsumerName, cancellationToken))
            return null;

        var job = await repository.FindJobAsync(command.AccountId, command.MergeJobId, cancellationToken)
            ?? throw new MergeCommandRejectedException("Merge job was not found.");
        var mainCounterparty = DeserializeAndVerify(job, command);

        if (MergeJobStatuses.IsTerminal(job.Status))
        {
            await repository.SaveTerminalAsync(job.AccountId, job, NewInbox(job.MessageId, timeProvider.GetUtcNow()), cancellationToken);
            return null;
        }

        var participantIds = command.DuplicateCounterpartyIds.Append(command.MainCounterpartyId).ToArray();
        if (!await repository.OwnsLocksAsync(job.AccountId, job.Id, participantIds, cancellationToken))
        {
            // The job cannot safely resume. Release the locks it still owns while
            // recording a terminal state, so those counterparties do not remain busy.
            var now = timeProvider.GetUtcNow();
            job.Status = MergeJobStatuses.Failed;
            job.CompletedAt = now;
            job.UpdatedAt = now;
            var operation = job.Operations
                .Where(item => !MergeOperationStatuses.IsTerminal(item.Status))
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();
            if (operation is not null)
            {
                operation.Status = MergeOperationStatuses.Failed;
                operation.ErrorCode = "MERGE_COUNTERPARTY_LOCK_LOST";
                operation.ErrorMessage = "Merge job no longer owns all counterparty locks.";
                operation.CompletedAt = now;
                operation.UpdatedAt = now;
            }
            await repository.SaveTerminalAsync(job.AccountId, job, NewInbox(job.MessageId, now), cancellationToken);
            return null;
        }

        return new MergeExecutionContext(job, mainCounterparty);
    }

    private static MergeMainCounterpartyDto DeserializeAndVerify(MergeJob job, MergeRequested command)
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
            !SnapshotsEqual(snapshot, command.MainCounterparty) ||
            !archiveIds.SequenceEqual(command.DuplicateCounterpartyIds))
        {
            throw new MergeCommandRejectedException("Merge command does not match its durable job.");
        }

        return snapshot;
    }

    private static bool SnapshotsEqual(MergeMainCounterpartyDto left, MergeMainCounterpartyDto right)
    {
        if (left.Name != right.Name || left.Email != right.Email || left.Phone != right.Phone ||
            left.Description != right.Description)
        {
            return false;
        }

        var leftAttributes = left.Attributes ?? [];
        var rightAttributes = right.Attributes ?? [];
        return leftAttributes.Count == rightAttributes.Count &&
               leftAttributes.Zip(rightAttributes).All(pair =>
                   pair.First.Id == pair.Second.Id &&
                   pair.First.Type == pair.Second.Type &&
                   pair.First.SourceCounterpartyId == pair.Second.SourceCounterpartyId &&
                   pair.First.Clear == pair.Second.Clear &&
                   pair.First.ValueJson == pair.Second.ValueJson &&
                   pair.First.FileJson == pair.Second.FileJson &&
                   MergeCounterpartyAttributesParser.ValuesEqual(pair.First.Value, pair.Second.Value) &&
                   MergeCounterpartyAttributesParser.ValuesEqual(pair.First.File, pair.Second.File));
    }

    private static InboxMessage NewInbox(Guid messageId, DateTimeOffset now) => new()
    {
        MessageId = messageId,
        ConsumerName = ConsumerName,
        ProcessedAt = now
    };
}
