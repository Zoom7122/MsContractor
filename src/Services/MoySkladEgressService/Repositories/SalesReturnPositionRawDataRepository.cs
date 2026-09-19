using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface ISalesReturnPositionRawDataRepository
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> salesReturnIds,
        CancellationToken cancellationToken);

    Task ReplaceAsync(
        Guid accountId,
        Guid salesReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnPositionRawDataRepository : ISalesReturnPositionRawDataRepository
{
    private readonly EgressDbContext _dbContext;

    public SalesReturnPositionRawDataRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> salesReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = salesReturnIds.Distinct().ToArray();
        var rows = await _dbContext.SalesReturnPositionRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.SalesReturnId))
            .ToListAsync(cancellationToken);
        var result = rows
            .GroupBy(item => item.SalesReturnId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<Guid, string>)group.ToDictionary(item => item.PositionId, item => item.RawJson));

        if (result.Count != ids.Length || result.Values.Any(positions => positions.Count == 0))
            throw new InvalidOperationException("Raw positions are missing for one or more salesreturn documents.");

        return result;
    }

    public async Task ReplaceAsync(
        Guid accountId,
        Guid salesReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.SalesReturnPositionRawData
            .Where(item => item.AccountId == accountId && item.SalesReturnId == salesReturnId)
            .ToListAsync(cancellationToken);
        _dbContext.SalesReturnPositionRawData.RemoveRange(existing);

        _dbContext.SalesReturnPositionRawData.AddRange(
            positions.Select(position => new SalesReturnPositionRawData
            {
                AccountId = accountId,
                SalesReturnId = salesReturnId,
                PositionId = position.Key,
                RawJson = position.Value
            }));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
