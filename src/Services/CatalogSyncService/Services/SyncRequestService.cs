using MsContractor.Contracts.Sync;
using MsContractor.CatalogSyncService.Messaging;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncRequestService
{
    Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken);
}

public sealed class SyncRequestService : ISyncRequestService
{
    private readonly ISyncKafkaPublisher _publisher;
    private readonly TimeProvider _timeProvider;

    public SyncRequestService(
        ISyncKafkaPublisher publisher,
        TimeProvider timeProvider)
    {
        _publisher = publisher;
        _timeProvider = timeProvider;
    }

    public async Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken)
    {
        var command = new SyncRequested(Guid.NewGuid(), Guid.NewGuid(), request.AccountId,
            request.RequestedByUserId, _timeProvider.GetUtcNow(), request.Mode);
        await _publisher.PublishAsync(command, cancellationToken);
        return new SyncAccepted(command.SyncRunId, "queued");
    }
}
