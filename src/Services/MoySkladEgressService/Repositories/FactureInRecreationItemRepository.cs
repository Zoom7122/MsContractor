using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IFactureInRecreationItemRepository
{
    Task<IReadOnlyDictionary<Guid, FactureInRecreationItem>> GetAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> sourceFactureInIds,
        CancellationToken cancellationToken);

    Task<FactureInRecreationItem> CreateOrGetAsync(
        FactureInRecreationItem item,
        CancellationToken cancellationToken);

    Task SaveAsync(FactureInRecreationItem item, CancellationToken cancellationToken);
}

public sealed class FactureInRecreationItemRepository : IFactureInRecreationItemRepository
{
    private readonly EgressDbContext _dbContext;

    public FactureInRecreationItemRepository(EgressDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyDictionary<Guid, FactureInRecreationItem>> GetAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> sourceFactureInIds,
        CancellationToken cancellationToken)
    {
        var ids = sourceFactureInIds.Distinct().ToArray();
        return await _dbContext.FactureInRecreationItems
            .Where(item => item.AccountId == accountId && ids.Contains(item.SourceFactureInId))
            .ToDictionaryAsync(item => item.SourceFactureInId, cancellationToken);
    }

    public async Task<FactureInRecreationItem> CreateOrGetAsync(
        FactureInRecreationItem item,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.FactureInRecreationItems.FindAsync(
            [item.AccountId, item.SourceFactureInId], cancellationToken);
        if (existing is not null)
            return existing;

        _dbContext.FactureInRecreationItems.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task SaveAsync(FactureInRecreationItem item, CancellationToken cancellationToken)
    {
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
