using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface IMergeOutboxRepository
{
    Task<IReadOnlyList<SyncOutboxMessage>> GetPendingAsync(CancellationToken cancellationToken);
    Task SavePublicationResultsAsync(CancellationToken cancellationToken);
}

// Infrastructure dispatch deliberately reads pending messages for every account.
public sealed class MergeOutboxRepository(CatalogSyncDbContext dbContext) : IMergeOutboxRepository
{
    public async Task<IReadOnlyList<SyncOutboxMessage>> GetPendingAsync(CancellationToken cancellationToken) =>
        await dbContext.OutboxMessages.Where(x => x.PublishedAt == null && x.EventType == nameof(MergeRequested))
            .OrderBy(x => x.CreatedAt).Take(50).ToListAsync(cancellationToken);

    public async Task SavePublicationResultsAsync(CancellationToken cancellationToken) =>
        await dbContext.SaveChangesAsync(cancellationToken);
}
