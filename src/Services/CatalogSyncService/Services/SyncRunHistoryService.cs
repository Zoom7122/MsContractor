using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncRunHistoryService
{
    Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken);
    Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed class SyncRunHistoryService : ISyncRunHistoryService
{
    private readonly ISyncRunHistoryRepository _repository;

    public SyncRunHistoryService(ISyncRunHistoryRepository repository)
    {
        _repository = repository;
    }

    public Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
        => _repository.GetAsync(accountId, page, pageSize, cancellationToken);

    public Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, CancellationToken cancellationToken)
        => _repository.GetErrorsAsync(accountId, syncRunId, page, pageSize, cancellationToken);
}
