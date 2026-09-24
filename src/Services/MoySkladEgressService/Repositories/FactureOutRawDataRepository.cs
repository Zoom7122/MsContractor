using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IFactureOutRawDataRepository
{
    Task UpsertAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken);
}

public sealed class FactureOutRawDataRepository : IFactureOutRawDataRepository
{
    private readonly EgressDbContext _dbContext;

    public FactureOutRawDataRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
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
            throw new ArgumentException("Raw factureout documents must have non-empty ids and JSON.", nameof(documents));

        var ids = documents.Keys.ToArray();
        var existing = await _dbContext.FactureOutRawData
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, cancellationToken);

        foreach (var document in documents)
        {
            if (existing.TryGetValue(document.Key, out var row))
            {
                row.RawJson = document.Value;
                continue;
            }

            _dbContext.FactureOutRawData.Add(new FactureOutRawData
            {
                AccountId = accountId,
                DocumentId = document.Key,
                RawJson = document.Value
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
