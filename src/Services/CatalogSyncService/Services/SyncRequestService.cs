using MsContractor.Contracts.Sync;
using MsContractor.CatalogSyncService.Messaging;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncRequestService
{
    Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken);
}

public sealed class SyncRequestService(ISyncKafkaPublisher publisher, TimeProvider timeProvider) : ISyncRequestService
{
    public async Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken)
    {
        var command = new SyncRequested(Guid.NewGuid(), Guid.NewGuid(), request.AccountId,
            request.RequestedByUserId, timeProvider.GetUtcNow(), request.Mode);
        await publisher.PublishAsync(command, cancellationToken);
        return new SyncAccepted(command.SyncRunId, "queued");
    }
}
