using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ISyncRunStateRepository
{
    Task<SyncRunState?> GetLatestAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class SyncRunStateRepository : ISyncRunStateRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public SyncRunStateRepository(CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SyncRunState?> GetLatestAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.SyncRuns
            .AsNoTracking()
            .Where(run => run.AccountId == accountId)
            .OrderByDescending(run => run.CreatedAt)
            .ThenByDescending(run => run.Id)
            .Select(run => new SyncRunState(
                run.Status,
                run.TotalCount,
                run.CreatedAt,
                run.UpdatedAt,
                run.ExecutionMode,
                run.ErrorCode,
                run.ErrorMessage))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
