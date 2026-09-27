using MsContractor.CatalogSyncService.Clients;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public interface IStatePreparationService
{
    Task<CatalogStateResponse> PrepareAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken);
}

public sealed class StatePreparationService : IStatePreparationService
{
    private readonly ISyncRunStateRepository _syncRunRepository;
    private readonly IMergeJobStateRepository _mergeJobRepository;
    private readonly IMoySkladEgressClient _egressClient;
    private readonly TimeProvider _timeProvider;

    public StatePreparationService(
        ISyncRunStateRepository syncRunRepository,
        IMergeJobStateRepository mergeJobRepository,
        IMoySkladEgressClient egressClient,
        TimeProvider timeProvider)
    {
        _syncRunRepository = syncRunRepository;
        _mergeJobRepository = mergeJobRepository;
        _egressClient = egressClient;
        _timeProvider = timeProvider;
    }

    public async Task<CatalogStateResponse> PrepareAsync(
        Guid accountId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var latestSyncRun = await _syncRunRepository.GetLatestAsync(accountId, cancellationToken);
        var counterpartyCount = await _syncRunRepository.GetCounterpartyCountAsync(accountId, cancellationToken);
        var latestMergeJobs = await _mergeJobRepository.GetLatestAsync(accountId, cancellationToken);

        MoySkladConnectionState moySkladState;
        try
        {
            var result = await _egressClient.CheckConnectionAsync(
                accountId,
                correlationId,
                cancellationToken);
            moySkladState = new MoySkladConnectionState(result.Connected, null, null);
        }
        catch (EgressClientException exception)
        {
            moySkladState = new MoySkladConnectionState(
                false,
                exception.Code,
                exception.SafeMessage);
        }

        return new CatalogStateResponse(
            accountId,
            _timeProvider.GetUtcNow(),
            DatabaseConnected: true,
            counterpartyCount,
            latestSyncRun,
            latestMergeJobs,
            moySkladState);
    }
}
