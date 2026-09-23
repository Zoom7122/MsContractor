using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IPurchaseReturnFactureRelationsRepository
{
    Task<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>> GetSnapshotsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken);
}

public interface IPurchaseReturnMoneyRelationsRepository
{
    Task SaveMoneyRelationSnapshotsAsync(
        Guid accountId,
        IReadOnlyCollection<PurchaseReturnMoneyRelationSnapshotUpdate> snapshots,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnFactureRelationsRepository :
    IPurchaseReturnFactureRelationsRepository,
    IPurchaseReturnMoneyRelationsRepository
{
    private readonly EgressDbContext _dbContext;

    public PurchaseReturnFactureRelationsRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>> GetSnapshotsAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> purchaseReturnIds,
        CancellationToken cancellationToken)
    {
        var ids = purchaseReturnIds.Distinct().ToArray();
        if (ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Purchase return ids must be non-empty.", nameof(purchaseReturnIds));

        var factureIn = await _dbContext.PurchaseReturnFactureInRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var factureOut = await _dbContext.PurchaseReturnFactureOutRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var paymentIns = await _dbContext.PurchaseReturnPaymentInRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);
        var cashIns = await _dbContext.PurchaseReturnCashInRawData
            .AsNoTracking()
            .Where(item => item.AccountId == accountId && ids.Contains(item.PurchaseReturnId))
            .ToListAsync(cancellationToken);

        return ids.ToDictionary(
            id => id,
            id => new PurchaseReturnFactureRelationsSnapshot(
                id,
                factureIn
                    .Where(item => item.PurchaseReturnId == id)
                    .OrderBy(item => item.DocumentId)
                    .Select(item => item.RawJson)
                    .ToArray(),
                factureOut
                    .Where(item => item.PurchaseReturnId == id)
                    .OrderBy(item => item.DocumentId)
                    .Select(item => item.RawJson)
                    .ToArray(),
                paymentIns
                    .Where(item => item.PurchaseReturnId == id)
                    .OrderBy(item => item.DocumentId)
                    .Select(item => new PurchaseReturnMoneyRelationSnapshot(
                        item.DocumentId,
                        item.RawJson,
                        item.OperationsBeforeJson,
                        item.LinkedSum))
                    .ToArray(),
                cashIns
                    .Where(item => item.PurchaseReturnId == id)
                    .OrderBy(item => item.DocumentId)
                    .Select(item => new PurchaseReturnMoneyRelationSnapshot(
                        item.DocumentId,
                        item.RawJson,
                        item.OperationsBeforeJson,
                        item.LinkedSum))
                    .ToArray()));
    }

    public async Task SaveMoneyRelationSnapshotsAsync(
        Guid accountId,
        IReadOnlyCollection<PurchaseReturnMoneyRelationSnapshotUpdate> snapshots,
        CancellationToken cancellationToken)
    {
        if (snapshots.Count == 0)
            return;

        var sourceIds = snapshots.Select(item => item.PurchaseReturnId).Distinct().ToArray();
        var paymentIds = snapshots
            .Where(item => !item.IsCashIn)
            .Select(item => item.DocumentId)
            .Distinct()
            .ToArray();
        var cashIds = snapshots
            .Where(item => item.IsCashIn)
            .Select(item => item.DocumentId)
            .Distinct()
            .ToArray();

        var paymentRows = await _dbContext.PurchaseReturnPaymentInRawData
            .Where(item => item.AccountId == accountId && sourceIds.Contains(item.PurchaseReturnId) && paymentIds.Contains(item.DocumentId))
            .ToListAsync(cancellationToken);
        var cashRows = await _dbContext.PurchaseReturnCashInRawData
            .Where(item => item.AccountId == accountId && sourceIds.Contains(item.PurchaseReturnId) && cashIds.Contains(item.DocumentId))
            .ToListAsync(cancellationToken);

        var paymentByKey = paymentRows.ToDictionary(item => (item.PurchaseReturnId, item.DocumentId));
        var cashByKey = cashRows.ToDictionary(item => (item.PurchaseReturnId, item.DocumentId));
        foreach (var snapshot in snapshots)
        {
            var key = (snapshot.PurchaseReturnId, snapshot.DocumentId);
            if (snapshot.IsCashIn)
            {
                if (!cashByKey.TryGetValue(key, out var row))
                    throw new InvalidOperationException($"Cashin relation row is missing for purchasereturn {snapshot.PurchaseReturnId:D} and document {snapshot.DocumentId:D}.");
                row.OperationsBeforeJson = snapshot.OperationsBeforeJson;
                row.LinkedSum = snapshot.LinkedSum;
            }
            else
            {
                if (!paymentByKey.TryGetValue(key, out var row))
                    throw new InvalidOperationException($"Paymentin relation row is missing for purchasereturn {snapshot.PurchaseReturnId:D} and document {snapshot.DocumentId:D}.");
                row.OperationsBeforeJson = snapshot.OperationsBeforeJson;
                row.LinkedSum = snapshot.LinkedSum;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
