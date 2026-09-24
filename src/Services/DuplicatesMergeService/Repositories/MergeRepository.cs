using Microsoft.EntityFrameworkCore;
using Npgsql;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface IMergeRepository
{
    Task<bool> HasProcessedAsync(Guid accountId, Guid messageId, string consumerName, CancellationToken cancellationToken);
    Task<MergeJob?> FindJobAsync(Guid accountId, Guid jobId, CancellationToken cancellationToken);
    Task CreateAsync(Guid accountId, MergeJob job, SyncOutboxMessage outbox,
        IReadOnlyCollection<Guid> counterpartyIds, DateTimeOffset acquiredAt, CancellationToken cancellationToken);
    Task<bool> OwnsLocksAsync(Guid accountId, Guid jobId, IReadOnlyCollection<Guid> counterpartyIds, CancellationToken cancellationToken);
    Task InsertOperationAsync(Guid accountId, MergeJob job, MergeOperation operation, CancellationToken cancellationToken);
    Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken);
    Task SaveTerminalAsync(Guid accountId, MergeJob job, InboxMessage inbox, CancellationToken cancellationToken);
}

public sealed class MergeRepository : IMergeRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public MergeRepository(
        CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> HasProcessedAsync(Guid accountId, Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.ConsumerName == consumerName, cancellationToken);
    }
    public async Task<MergeJob?> FindJobAsync(Guid accountId, Guid jobId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.MergeJobs.Include(x => x.Operations).SingleOrDefaultAsync(x => x.AccountId == accountId && x.Id == jobId, cancellationToken);
    }
    public async Task CreateAsync(Guid accountId, MergeJob job, SyncOutboxMessage outbox,
        IReadOnlyCollection<Guid> counterpartyIds, DateTimeOffset acquiredAt, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (job.AccountId != accountId || job.Operations.Any(x => x.AccountId != accountId) ||
            counterpartyIds.Count == 0 || counterpartyIds.Any(id => id == Guid.Empty) ||
            counterpartyIds.Distinct().Count() != counterpartyIds.Count)
            throw new InvalidOperationException("Merge job belongs to another account.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        _dbContext.MergeJobs.Add(job);
        _dbContext.OutboxMessages.Add(outbox);
        _dbContext.MergeCounterpartyLocks.AddRange(counterpartyIds.Select(id => new MergeCounterpartyLock
        {
            AccountId = accountId,
            CounterpartyId = id,
            MergeJobId = job.Id,
            MergeJob = job,
            AcquiredAt = acquiredAt
        }));
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsLockConflict(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw new Models.Exceptions.MergeRequestException(
                Models.MergeRequestError.CounterpartyBusy,
                "COUNTERPARTY_BUSY",
                "One or more counterparties are already being merged.");
        }
    }
    public async Task<bool> OwnsLocksAsync(Guid accountId, Guid jobId, IReadOnlyCollection<Guid> counterpartyIds, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var ownedIds = await _dbContext.MergeCounterpartyLocks.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.MergeJobId == jobId)
            .Select(x => x.CounterpartyId)
            .ToListAsync(cancellationToken);
        return ownedIds.Count == counterpartyIds.Count && ownedIds.ToHashSet().SetEquals(counterpartyIds);
    }
    public async Task InsertOperationAsync(Guid accountId, MergeJob job, MergeOperation operation, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (job.AccountId != accountId || operation.AccountId != accountId || operation.MergeJobId != job.Id)
            throw new InvalidOperationException("Merge operation belongs to another job or account.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Shift from the end, saving each step to preserve the unique sequence index.
        foreach (var existing in job.Operations.Where(x => x.Sequence >= operation.Sequence).OrderByDescending(x => x.Sequence))
        {
            existing.Sequence++;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        job.Operations.Add(operation);
        _dbContext.MergeOperations.Add(operation);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
    public async Task SaveTerminalAsync(Guid accountId, MergeJob job, InboxMessage inbox, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (job.AccountId != accountId || !MergeJobStatuses.IsTerminal(job.Status))
            throw new InvalidOperationException("Merge job must be terminal before releasing its locks.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.MergeCounterpartyLocks
            .Where(x => x.AccountId == accountId && x.MergeJobId == job.Id)
            .ExecuteDeleteAsync(cancellationToken);
        if (!_dbContext.InboxMessages.Local.Any(x => x.MessageId == inbox.MessageId && x.ConsumerName == inbox.ConsumerName))
            _dbContext.InboxMessages.Add(inbox);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsLockConflict(DbUpdateException exception) =>
        exception.Entries.Any(entry => entry.Entity is MergeCounterpartyLock) &&
        (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_merge_counterparty_locks" } ||
         exception.InnerException?.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" &&
         exception.InnerException.Message.Contains("merge_counterparty_locks", StringComparison.Ordinal));
}
