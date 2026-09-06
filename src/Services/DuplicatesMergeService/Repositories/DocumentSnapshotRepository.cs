using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;

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
    Task<IReadOnlyDictionary<Guid, Guid?>> GetCommissionsAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
    Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, IReadOnlyCollection<DocumentAdditionalData> additionalData, CancellationToken cancellationToken);
    Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken);
}

public sealed class DocumentSnapshotRepository(CatalogSyncDbContext dbContext) : IDocumentSnapshotRepository
{
    public async Task<IReadOnlyList<CounterpartyDocument>> GetForCounterpartiesAsync(Guid accountId, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && ids.Contains(x.CounterpartyId))
            .OrderBy(x => x.DocumentType).ThenBy(x => x.DocumentId).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetCommissionsAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.DocumentAdditionalCommissions
            .Where(x => documentIds.Contains(x.DocumentId) && dbContext.CounterpartyDocuments.Any(d => d.AccountId == accountId && d.DocumentId == x.DocumentId))
            .ToDictionaryAsync(x => x.DocumentId, x => x.Contract, cancellationToken);
    }
    public async Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, IReadOnlyCollection<DocumentAdditionalData> additionalData, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        var documentIds = rows.Select(x => x.DocumentId).ToHashSet();
        if (rows.Any(x => x.AccountId != accountId || !counterpartyIds.Contains(x.CounterpartyId)) ||
            commissions.Any(x => !documentIds.Contains(x.DocumentId)) || additionalData.Any(x => !documentIds.Contains(x.DocumentId)))
            throw new InvalidOperationException("Document snapshot belongs to another account or selection.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && counterpartyIds.Contains(x.CounterpartyId)).ExecuteDeleteAsync(cancellationToken);
        dbContext.CounterpartyDocuments.AddRange(rows);
        dbContext.DocumentAdditionalCommissions.AddRange(commissions);
        dbContext.DocumentAdditionalData.AddRange(additionalData);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
