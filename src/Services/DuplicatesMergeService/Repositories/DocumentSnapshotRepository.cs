using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.DuplicatesMergeService.Models;

namespace MsContractor.DuplicatesMergeService.Repositories;

public interface IDocumentSnapshotRepository
{
    /// <summary>
    /// выбирает документы по ID дубликатов, с фильтром по текущему аккаунту. А обрабатываются сначала комиссионные отчёты, затем остальные документы
    /// </summary>
    /// <param name="accountId"></param>
    /// <param name="ids"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IReadOnlyList<CounterpartyDocument>> GetForCounterpartiesAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<(string DocumentType, Guid DocumentId), Guid?>> GetCommissionsAsync(
        Guid accountId, IReadOnlyCollection<(string DocumentType, Guid DocumentId)> documentKeys, CancellationToken cancellationToken);
    Task ReplaceDocumentRowsAsync(Guid accountId, IReadOnlyCollection<(string DocumentType, Guid DocumentId)> sourceDocumentKeys,
        IReadOnlyCollection<CounterpartyDocument> rows, CancellationToken cancellationToken);
    Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, CancellationToken cancellationToken);
    Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class DocumentSnapshotRepository : IDocumentSnapshotRepository
{
    private readonly CatalogSyncDbContext _dbContext;

    public DocumentSnapshotRepository(
        CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<CounterpartyDocument>> GetForCounterpartiesAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && ids.Contains(x.CounterpartyId))
            .OrderBy(x => x.DocumentType).ThenBy(x => x.DocumentId).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyDictionary<(string DocumentType, Guid DocumentId), Guid?>> GetCommissionsAsync(
        Guid accountId, IReadOnlyCollection<(string DocumentType, Guid DocumentId)> documentKeys, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var requestedKeys = documentKeys.ToHashSet();
        var rows = new List<DocumentAdditionalCommission>();
        foreach (var keyGroup in requestedKeys.GroupBy(key => key.DocumentType))
        {
            var documentIds = keyGroup.Select(key => key.DocumentId).ToArray();
            rows.AddRange(await _dbContext.DocumentAdditionalCommissions
                .Where(x => x.AccountId == accountId && x.DocumentType == keyGroup.Key &&
                            documentIds.Contains(x.DocumentId))
                .ToListAsync(cancellationToken));
        }
        return rows
            .Where(x => requestedKeys.Contains((x.DocumentType, x.DocumentId)))
            .ToDictionary(x => (x.DocumentType, x.DocumentId), x => x.Contract);
    }

    public async Task ReplaceDocumentRowsAsync(Guid accountId,
        IReadOnlyCollection<(string DocumentType, Guid DocumentId)> sourceDocumentKeys,
        IReadOnlyCollection<CounterpartyDocument> rows, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (sourceDocumentKeys.Distinct().Count() != sourceDocumentKeys.Count ||
            rows.Count > sourceDocumentKeys.Count ||
            rows.Any(x => x.AccountId != accountId) ||
            rows.Select(x => (x.DocumentType, x.DocumentId)).Distinct().Count() != rows.Count)
            throw new InvalidOperationException("Document snapshot replacement is invalid.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var keysToDelete = sourceDocumentKeys
            .Concat(rows.Select(row => (row.DocumentType, row.DocumentId)))
            .ToHashSet();
        foreach (var keyGroup in keysToDelete.GroupBy(key => key.DocumentType))
        {
            var ids = keyGroup.Select(key => key.DocumentId).ToArray();
            await _dbContext.CounterpartyDocuments
                .Where(x => x.AccountId == accountId && x.DocumentType == keyGroup.Key && ids.Contains(x.DocumentId))
                .ExecuteDeleteAsync(cancellationToken);
        }
        _dbContext.CounterpartyDocuments.AddRange(rows);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var documentKeys = rows.Select(x => (x.DocumentType, x.DocumentId)).ToHashSet();
        if (rows.Any(x => x.AccountId != accountId || !counterpartyIds.Contains(x.CounterpartyId)) ||
            rows.Select(x => (x.DocumentType, x.DocumentId)).Distinct().Count() != rows.Count ||
            commissions.Any(x => x.AccountId != accountId || !documentKeys.Contains((x.DocumentType, x.DocumentId))) ||
            commissions.Select(x => (x.DocumentType, x.DocumentId)).Distinct().Count() != commissions.Count)
            throw new InvalidOperationException("Document snapshot belongs to another account or selection.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.CounterpartyDocuments
            .Where(x => x.AccountId == accountId && counterpartyIds.Contains(x.CounterpartyId))
            .ExecuteDeleteAsync(cancellationToken);
        foreach (var keyGroup in documentKeys.GroupBy(key => key.DocumentType))
        {
            var ids = keyGroup.Select(key => key.DocumentId).ToArray();
            await _dbContext.CounterpartyDocuments
                .Where(x => x.AccountId == accountId && x.DocumentType == keyGroup.Key && ids.Contains(x.DocumentId))
                .ExecuteDeleteAsync(cancellationToken);
        }
        _dbContext.CounterpartyDocuments.AddRange(rows);
        _dbContext.DocumentAdditionalCommissions.AddRange(commissions);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
