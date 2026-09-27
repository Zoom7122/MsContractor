using Microsoft.EntityFrameworkCore;
using MsContractor.Contracts.Internal;
using MsContractor.CatalogSyncService.Persistence;

namespace MsContractor.CatalogSyncService.Repositories;

public interface IMergeJobStateRepository
{
    Task<IReadOnlyList<MergeJobState>> GetLatestAsync(
        Guid accountId,
        CancellationToken cancellationToken);
}

public sealed class MergeJobStateRepository : IMergeJobStateRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public MergeJobStateRepository(CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<MergeJobState>> GetLatestAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var jobs = await _dbContext.MergeJobs
            .AsNoTracking()
            .Where(job => job.AccountId == accountId)
            .Include(job => job.Operations)
            .OrderByDescending(job => job.CreatedAt)
            .ThenByDescending(job => job.Id)
            .Take(100)
            .ToListAsync(cancellationToken);

        return jobs.Select(job => new MergeJobState(
                job.Id,
                job.MessageId,
                job.CorrelationId,
                job.AccountId,
                job.MainCounterpartyId,
                job.RequestedByUserId,
                job.Status,
                job.PayloadVersion,
                job.Payload,
                job.CreatedAt,
                job.StartedAt,
                job.CompletedAt,
                job.UpdatedAt,
                job.Operations
                    .OrderBy(operation => operation.Sequence)
                    .Select(operation => new MergeOperationState(
                        operation.Id,
                        operation.OperationType,
                        operation.CounterpartyId,
                        operation.Status,
                        operation.AttemptCount,
                        operation.ErrorCode,
                        operation.ErrorMessage,
                        operation.StartedAt,
                        operation.CompletedAt))
                    .ToArray()))
            .ToArray();
    }
}
