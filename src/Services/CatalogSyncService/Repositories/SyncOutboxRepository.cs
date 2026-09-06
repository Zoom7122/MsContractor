using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Sync;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ISyncOutboxRepository
{
    Task<IReadOnlyList<SyncOutboxMessage>> GetPendingAsync(CancellationToken cancellationToken);
    Task SavePublicationResultsAsync(CancellationToken cancellationToken);
}

// Infrastructure dispatch deliberately reads pending messages for every account.
public sealed class SyncOutboxRepository(CatalogSyncDbContext dbContext) : ISyncOutboxRepository
{
    public async Task<IReadOnlyList<SyncOutboxMessage>> GetPendingAsync(CancellationToken cancellationToken) =>
        await dbContext.OutboxMessages.Where(x => x.PublishedAt == null && (x.EventType == nameof(SyncCompleted) || x.EventType == nameof(SyncFailed)))
            .OrderBy(x => x.CreatedAt).Take(50).ToListAsync(cancellationToken);

    public async Task SavePublicationResultsAsync(CancellationToken cancellationToken) =>
        await dbContext.SaveChangesAsync(cancellationToken);
}
