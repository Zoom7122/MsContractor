using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public enum MergeRequestError
{
    Invalid,
    NotFound,
    MainArchived
}

public sealed class MergeRequestException(MergeRequestError error, string code, string safeMessage)
    : Exception(safeMessage)
{
    public MergeRequestError Error { get; } = error;
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
}

public interface IMergeJobCreator
{
    Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid correlationId,
        CreateMergeJobRequest request,
        CancellationToken cancellationToken);
}

public sealed class MergeJobCreator(
    CatalogSyncDbContext dbContext,
    TimeProvider timeProvider) : IMergeJobCreator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MergeJobAccepted> CreateAsync(
        Guid accountId,
        Guid requestedByUserId,
        Guid correlationId,
        CreateMergeJobRequest request,
        CancellationToken cancellationToken)
    {
        ValidateShape(request);
        var ids = request.DuplicateCounterpartyIds.Append(request.MainCounterpartyId).ToArray();
        var counterparties = await dbContext.Counterparties
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.Id))
            .Select(item => new { item.Id, item.Archived })
            .ToListAsync(cancellationToken);
        if (counterparties.Count != ids.Length)
        {
            throw new MergeRequestException(
                MergeRequestError.NotFound,
                "COUNTERPARTY_NOT_FOUND",
                "One or more counterparties were not found.");
        }

        if (counterparties.Single(item => item.Id == request.MainCounterpartyId).Archived)
        {
            throw new MergeRequestException(
                MergeRequestError.MainArchived,
                "MAIN_COUNTERPARTY_ARCHIVED",
                "The main counterparty is archived.");
        }

        var now = timeProvider.GetUtcNow();
        var jobId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var command = new MergeRequested(
            1,
            messageId,
            correlationId,
            jobId,
            accountId,
            request.MainCounterpartyId,
            request.DuplicateCounterpartyIds,
            request.MainCounterparty,
            requestedByUserId,
            now);
        var job = new MergeJob
        {
            Id = jobId,
            MessageId = messageId,
            CorrelationId = correlationId,
            AccountId = accountId,
            MainCounterpartyId = request.MainCounterpartyId,
            RequestedByUserId = requestedByUserId,
            Status = MergeJobStatuses.Pending,
            PayloadVersion = 1,
            Payload = JsonSerializer.Serialize(request.MainCounterparty, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };
        job.Operations.Add(NewOperation(job, 0, MergeOperationTypes.UpdateMainCounterparty, request.MainCounterpartyId, now));
        for (var index = 0; index < request.DuplicateCounterpartyIds.Count; index++)
        {
            job.Operations.Add(NewOperation(
                job,
                index + 1,
                MergeOperationTypes.ArchiveDuplicate,
                request.DuplicateCounterpartyIds[index],
                now));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.MergeJobs.Add(job);
        dbContext.OutboxMessages.Add(new SyncOutboxMessage
        {
            Id = messageId,
            Topic = MergeTopics.Commands,
            MessageKey = accountId.ToString("D"),
            EventType = nameof(MergeRequested),
            Payload = JsonSerializer.Serialize(command, JsonOptions),
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MergeJobAccepted(jobId, MergeJobStatuses.Pending);
    }

    private static MergeOperation NewOperation(
        MergeJob job,
        int sequence,
        string type,
        Guid counterpartyId,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        MergeJob = job,
        MergeJobId = job.Id,
        AccountId = job.AccountId,
        Sequence = sequence,
        OperationType = type,
        CounterpartyId = counterpartyId,
        Status = MergeOperationStatuses.Pending,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static void ValidateShape(CreateMergeJobRequest request)
    {
        if (request.MainCounterpartyId == Guid.Empty ||
            request.MainCounterparty is null ||
            string.IsNullOrWhiteSpace(request.MainCounterparty.Name) ||
            request.MainCounterparty.Name.Length > 1024 ||
            request.MainCounterparty.Email?.Length > 320 ||
            request.MainCounterparty.Phone?.Length > 255 ||
            request.DuplicateCounterpartyIds is null ||
            request.DuplicateCounterpartyIds.Count == 0 ||
            request.DuplicateCounterpartyIds.Any(id => id == Guid.Empty) ||
            request.DuplicateCounterpartyIds.Distinct().Count() != request.DuplicateCounterpartyIds.Count ||
            request.DuplicateCounterpartyIds.Contains(request.MainCounterpartyId))
        {
            throw new MergeRequestException(
                MergeRequestError.Invalid,
                "INVALID_MERGE_REQUEST",
                "Merge request contains invalid fields.");
        }
    }
}
