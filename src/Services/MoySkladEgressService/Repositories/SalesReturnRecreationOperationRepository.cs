using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface ISalesReturnRecreationOperationRepository
{
    Task CreateAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken);

    Task SaveAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken);
}

public sealed class SalesReturnRecreationOperationRepository : ISalesReturnRecreationOperationRepository
{
    private readonly EgressDbContext _dbContext;

    public SalesReturnRecreationOperationRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task CreateAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken)
    {
        _dbContext.SalesReturnRecreationOperations.Add(operation);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(SalesReturnRecreationOperation operation, CancellationToken cancellationToken)
    {
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
