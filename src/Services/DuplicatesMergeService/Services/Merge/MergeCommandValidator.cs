using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;

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
            await repository.SaveInboxAsync(job.AccountId, NewInbox(job.MessageId, timeProvider.GetUtcNow()), cancellationToken);
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
}
