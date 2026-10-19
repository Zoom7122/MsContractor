using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public interface IDocumentsInOperationStateService
{
    Task<IReadOnlyList<DocumentsInOperationContent>> GetForMergeJobAsync(
        Guid accountId,
        Guid mergeJobId,
        CancellationToken cancellationToken);
}

public sealed class DocumentsInOperationStateService : IDocumentsInOperationStateService
{
    private readonly IDocumentsInOperationStateRepository _repository;

    public DocumentsInOperationStateService(IDocumentsInOperationStateRepository repository)
    {
        _repository = repository;
    }

    public Task<IReadOnlyList<DocumentsInOperationContent>> GetForMergeJobAsync(
        Guid accountId,
        Guid mergeJobId,
        CancellationToken cancellationToken) =>
        _repository.GetForMergeJobAsync(accountId, mergeJobId, cancellationToken);
}
