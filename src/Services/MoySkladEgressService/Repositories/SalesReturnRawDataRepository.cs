using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface ISalesReturnRawDataRepository
{
    Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken);

    Task UpsertAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRawDataRepository : ISalesReturnRawDataRepository
{
    private readonly EgressDbContext _dbContext;

    public SalesReturnRawDataRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        var result = await _dbContext.SalesReturnRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, item => item.RawJson, cancellationToken);

        if (result.Count != ids.Length)
            throw new InvalidOperationException("Raw data is missing for one or more salesreturn documents.");

        return result;
    }

    public async Task UpsertAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
            return;

        var ids = documents.Keys.ToArray();
        var existing = await _dbContext.SalesReturnRawData
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, cancellationToken);

        foreach (var document in documents)
        {
            if (existing.TryGetValue(document.Key, out var row))
            {
                row.RawJson = document.Value;
                continue;
            }

            _dbContext.SalesReturnRawData.Add(new SalesReturnRawData
            {
                AccountId = accountId,
                DocumentId = document.Key,
                RawJson = document.Value
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
