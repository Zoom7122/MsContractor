using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IPurchaseReturnPreparationRepository
{
    Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);

    Task UpsertDocumentsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken);

    Task ReplacePositionsAsync(
        Guid accountId,
        Guid purchaseReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnPreparationRepository : IPurchaseReturnPreparationRepository
{
    private readonly EgressDbContext _dbContext;

    public PurchaseReturnPreparationRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        var result = await _dbContext.PurchaseReturnRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, item => item.RawJson, cancellationToken);

        if (result.Count != ids.Length)
            throw new InvalidOperationException("Raw data is missing for one or more purchasereturn documents.");
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        var rows = await _dbContext.PurchaseReturnPositionRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var result = rows
            .GroupBy(item => item.PurchaseReturnId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<Guid, string>)group.ToDictionary(
                    item => item.PositionId,
                    item => item.RawJson));

        if (result.Count != ids.Length || result.Values.Any(positions => positions.Count == 0))
            throw new InvalidOperationException("Raw positions are missing for one or more purchasereturn documents.");
        return result;
    }

    public async Task UpsertDocumentsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, string> documents,
        CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
            return;

        var ids = documents.Keys.ToArray();
        var existing = await _dbContext.PurchaseReturnRawData
            .Where(item => item.AccountId == accountId && ids.Contains(item.DocumentId))
            .ToDictionaryAsync(item => item.DocumentId, cancellationToken);

        foreach (var document in documents)
        {
            if (existing.TryGetValue(document.Key, out var row))
            {
                row.RawJson = document.Value;
                continue;
            }

            _dbContext.PurchaseReturnRawData.Add(new PurchaseReturnRawData
            {
                AccountId = accountId,
                DocumentId = document.Key,
                RawJson = document.Value
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplacePositionsAsync(
        Guid accountId,
        Guid purchaseReturnId,
        IReadOnlyDictionary<Guid, string> positions,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.PurchaseReturnPositionRawData
            .Where(item => item.AccountId == accountId && item.PurchaseReturnId == purchaseReturnId)
            .ToListAsync(cancellationToken);
        _dbContext.PurchaseReturnPositionRawData.RemoveRange(existing);
        _dbContext.PurchaseReturnPositionRawData.AddRange(
            positions.Select(position => new PurchaseReturnPositionRawData
            {
                AccountId = accountId,
                PurchaseReturnId = purchaseReturnId,
                PositionId = position.Key,
                RawJson = position.Value
            }));

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
