using System.Data;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ISyncRunHistoryRepository
{
    Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken);
    Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class SyncRunHistoryRepository : ISyncRunHistoryRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public SyncRunHistoryRepository(CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var query = _dbContext.SyncRuns.AsNoTracking().Where(run => run.AccountId == accountId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(run => run.CreatedAt).ThenByDescending(run => run.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(SyncRunStateRepository.Projection)
            .ToListAsync(cancellationToken);
        return new SyncRunHistoryResponse(items, total, page, pageSize);
    }

    public async Task<SyncRunErrorsResponse?> GetErrorsAsync(
        Guid accountId, Guid syncRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var isolation = _dbContext.Database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(isolation, cancellationToken);
        var run = await _dbContext.SyncRuns.AsNoTracking()
            .Where(item => item.AccountId == accountId && item.Id == syncRunId)
            .Select(SyncRunStateRepository.Projection).SingleOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var items = await _dbContext.SyncRunErrors.AsNoTracking()
            .Where(error => error.AccountId == accountId && error.SyncRunId == syncRunId)
            .OrderByDescending(error => error.CreatedAt).ThenByDescending(error => error.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(error => new SyncRunErrorState(error.Id, error.CounterpartyId,
                error.ErrorMessage, error.ReasonCode, error.CreatedAt)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SyncRunErrorsResponse(run, items, run.ErrorsCount, page, pageSize);
    }
}
