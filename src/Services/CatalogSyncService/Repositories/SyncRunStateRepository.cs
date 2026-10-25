using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.CatalogSyncService.Persistence;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ISyncRunStateRepository
{
    Task<SyncRunState?> GetLatestAsync(Guid accountId, CancellationToken cancellationToken);
    Task<int> GetCounterpartyCountAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class SyncRunStateRepository : ISyncRunStateRepository
{
    internal static readonly Expression<Func<SyncRun, SyncRunState>> Projection = run => new SyncRunState(
        run.Id, run.Status, run.ProcessedCount, run.TotalCount, run.CreatedAt, run.StartedAt,
        run.UpdatedAt, run.ExecutionMode, run.ErrorCode, run.ErrorMessage, run.ErrorsCount, run.CompletedAt);
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
            .Select(Projection)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> GetCounterpartyCountAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties
            .CountAsync(counterparty => counterparty.AccountId == accountId, cancellationToken);
    }
}
