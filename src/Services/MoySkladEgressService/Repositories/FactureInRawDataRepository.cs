using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IFactureInRawDataRepository
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

public sealed class FactureInRawDataRepository : IFactureInRawDataRepository
{
    private readonly EgressDbContext _dbContext;

    public FactureInRawDataRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> documentIds,
        CancellationToken cancellationToken)
    {
        var ids = documentIds.Distinct().ToArray();
        var result = await _dbContext.FactureInRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, item => item.RawJson, cancellationToken);

        if (result.Count != ids.Length)
            throw new InvalidOperationException("Raw data is missing for one or more facturein documents.");

        return result;
    }

    public async Task UpsertAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
        if (documents.Count == 0)
            return;
        if (documents.Keys.Any(id => id == Guid.Empty || string.IsNullOrWhiteSpace(documents[id])))
            throw new ArgumentException("Raw facturein documents must have non-empty ids and JSON.", nameof(documents));

        var ids = documents.Keys.ToArray();
        var existing = await _dbContext.FactureInRawData
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, cancellationToken);

        foreach (var document in documents)
        {
            if (existing.TryGetValue(document.Key, out var row))
            {
                row.RawJson = document.Value;
                continue;
            }

            _dbContext.FactureInRawData.Add(new FactureInRawData
            {
                AccountId = accountId,
                DocumentId = document.Key,
                RawJson = document.Value
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
