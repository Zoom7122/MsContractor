using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Repositories;

public interface IDocumentsInOperationStateRepository
{
    Task<IReadOnlyList<DocumentsInOperationContent>> GetForMergeJobAsync(
        Guid accountId,
        Guid mergeJobId,
        CancellationToken cancellationToken);
}

public sealed class DocumentsInOperationStateRepository : IDocumentsInOperationStateRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public DocumentsInOperationStateRepository(CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<DocumentsInOperationContent>> GetForMergeJobAsync(
        Guid accountId,
        Guid mergeJobId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);

        return await _dbContext.DocumentsInOperation
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && item.MergeJobId == mergeJobId)
            .OrderBy(item => item.OperationType)
            .Select(item => new DocumentsInOperationContent(item.OperationType, item.RawJson))
            .ToListAsync(cancellationToken);
    }
}
