using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.DuplicatesMergeService.Models;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface ICounterpartyRepository
{
    Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartyDisplayItem>> GetDisplayItemsAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartySelectionItem>> GetSelectionAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<CounterpartyAvailability>> GetAvailabilityAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<Counterparty?> FindTrackedAsync(Guid accountId, Guid id, CancellationToken cancellationToken);
}

public sealed class CounterpartyRepository(CatalogSyncDbContext dbContext) : ICounterpartyRepository
{
    public async Task<IReadOnlyList<DuplicateCandidate>> GetCandidatesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && !x.Archived)
            .Select(x => new DuplicateCandidate(x.Id, x.NormalizedName, x.NormalizedEmail, x.NormalizedPhone)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<CounterpartyDisplayItem>> GetDisplayItemsAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartyDisplayItem(x.Id, x.Name, x.Email, x.Phone, x.Description, x.RawJson, x.CreatedAt, x.UpdatedAt)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<CounterpartySelectionItem>> GetSelectionAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartySelectionItem(x.Id, x.Name, x.Description, x.Email, x.Phone, x.Archived, x.UpdatedAt)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<CounterpartyAvailability>> GetAvailabilityAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.Counterparties.AsNoTracking().Where(x => x.AccountId == accountId && ids.Contains(x.Id))
            .Select(x => new CounterpartyAvailability(x.Id, x.Archived)).ToListAsync(cancellationToken);
    }
    public async Task<Counterparty?> FindTrackedAsync(Guid accountId, Guid id, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.Counterparties.SingleOrDefaultAsync(x => x.AccountId == accountId && x.Id == id, cancellationToken);
    }
}
