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
    Task<IReadOnlyDictionary<Guid, Guid?>> GetCommissionsAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
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
        return await _dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && ids.Contains(x.CounterpartyId) && x.DocumentType != "salesreturn")
            .OrderBy(x => x.DocumentType).ThenBy(x => x.DocumentId).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetCommissionsAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.DocumentAdditionalCommissions
            .Where(x => documentIds.Contains(x.DocumentId) && _dbContext.CounterpartyDocuments.Any(d => d.AccountId == accountId && d.DocumentId == x.DocumentId))
            .ToDictionaryAsync(x => x.DocumentId, x => x.Contract, cancellationToken);
    }
    public async Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var documentIds = rows.Select(x => x.DocumentId).ToHashSet();
        if (rows.Any(x => x.AccountId != accountId || !counterpartyIds.Contains(x.CounterpartyId)) ||
            commissions.Any(x => !documentIds.Contains(x.DocumentId)))
            throw new InvalidOperationException("Document snapshot belongs to another account or selection.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && counterpartyIds.Contains(x.CounterpartyId)).ExecuteDeleteAsync(cancellationToken);
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
