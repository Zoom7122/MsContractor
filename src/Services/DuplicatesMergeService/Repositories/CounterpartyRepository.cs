using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.DuplicatesMergeService.Models;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface ICounterpartyRepository
{
    Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogSettingExclusion>> GetDuplicateSearchExclusionsAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartyDisplayItem>> GetDisplayItemsAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartySelectionItem>> GetSelectionAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartyAvailability>> GetAvailabilityAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<bool> HasMergeLocksAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<Counterparty?> FindTrackedAsync(Guid accountId, Guid id, CancellationToken cancellationToken);
}

public sealed class CounterpartyRepository : ICounterpartyRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public CounterpartyRepository(
        CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId &&
                (!x.Archived || _dbContext.CounterpartyDocuments.Any(document =>
                    document.AccountId == accountId && document.CounterpartyId == x.Id)) &&
                !_dbContext.MergeCounterpartyLocks.Any(l => l.AccountId == accountId && l.CounterpartyId == x.Id))
            .Select(x => new DuplicateCandidate(x.Id, x.NormalizedName, x.NormalizedEmail, x.NormalizedPhone)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CatalogSettingExclusion>> GetDuplicateSearchExclusionsAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.CatalogSettingExclusions.AsNoTracking()
            .Where(item => item.AccountId == accountId)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CounterpartyDisplayItem>> GetDisplayItemsAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartyDisplayItem(x.Id, x.Name, x.Email, x.Phone, x.Description, x.Archived, x.RawJson, x.CreatedAt, x.UpdatedAt)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<CounterpartySelectionItem>> GetSelectionAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartySelectionItem(x.Id, x.Name, x.Description, x.Email, x.Phone, x.Archived, x.RawJson, x.UpdatedAt)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<CounterpartyAvailability>> GetAvailabilityAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartyAvailability(x.Id, x.Archived)).ToListAsync(cancellationToken);
    }
    public async Task<bool> HasMergeLocksAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.MergeCounterpartyLocks.AsNoTracking()
            .AnyAsync(x => x.AccountId == accountId && ids.Contains(x.CounterpartyId), cancellationToken);
    }
    public async Task<Counterparty?> FindTrackedAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Counterparties.SingleOrDefaultAsync(x => x.AccountId == accountId && x.Id == id, cancellationToken);
    }
}
