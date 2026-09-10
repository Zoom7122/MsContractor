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
    Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, IReadOnlyCollection<DocumentAdditionalData> additionalData, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, string>> GetAdditionalDataAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
    Task ApplyRecreatedAsync(Guid accountId, Guid mainId, IReadOnlyList<RecreatedDocumentSnapshot> documents, CancellationToken cancellationToken);
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
    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetCommissionsAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.DocumentAdditionalCommissions
            .Where(x => documentIds.Contains(x.DocumentId) && _dbContext.CounterpartyDocuments.Any(d => d.AccountId == accountId && d.DocumentId == x.DocumentId))
            .ToDictionaryAsync(x => x.DocumentId, x => x.Contract, cancellationToken);
    }
    public async Task ReplaceAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds, IReadOnlyCollection<CounterpartyDocument> rows, IReadOnlyCollection<DocumentAdditionalCommission> commissions, IReadOnlyCollection<DocumentAdditionalData> additionalData, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var documentIds = rows.Select(x => x.DocumentId).ToHashSet();
        if (rows.Any(x => x.AccountId != accountId || !counterpartyIds.Contains(x.CounterpartyId)) ||
            commissions.Any(x => !documentIds.Contains(x.DocumentId)) || additionalData.Any(x => !documentIds.Contains(x.DocumentId)))
            throw new InvalidOperationException("Document snapshot belongs to another account or selection.");
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await _dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId && counterpartyIds.Contains(x.CounterpartyId)).ExecuteDeleteAsync(cancellationToken);
        _dbContext.CounterpartyDocuments.AddRange(rows);
        _dbContext.DocumentAdditionalCommissions.AddRange(commissions);
        _dbContext.DocumentAdditionalData.AddRange(additionalData);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task<IReadOnlyDictionary<Guid, string>> GetAdditionalDataAsync(Guid accountId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.DocumentAdditionalData.AsNoTracking().Where(x => documentIds.Contains(x.DocumentId) &&
            _dbContext.CounterpartyDocuments.Any(d => d.AccountId == accountId && d.DocumentType == "salesreturn" && d.DocumentId == x.DocumentId))
            .ToDictionaryAsync(x => x.DocumentId, x => x.RawJson, cancellationToken);
    }

    public async Task ApplyRecreatedAsync(Guid accountId, Guid mainId, IReadOnlyList<RecreatedDocumentSnapshot> documents, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (documents.Count == 0) return;
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        foreach (var document in documents)
        {
            var old = await _dbContext.CounterpartyDocuments.SingleOrDefaultAsync(x => x.AccountId == accountId &&
                x.DocumentType == "salesreturn" && x.DocumentId == document.OldId, cancellationToken);
            var replacement = await _dbContext.CounterpartyDocuments.SingleOrDefaultAsync(x => x.AccountId == accountId &&
                x.DocumentType == "salesreturn" && x.DocumentId == document.NewId, cancellationToken);
            if (replacement is not null)
            {
                if (old is not null || replacement.CounterpartyId != mainId)
                    throw new InvalidOperationException("Recreated document conflicts with the local snapshot.");
                continue; // A replay after committing a partial Egress response.
            }
            if (old is null || old.CounterpartyId != document.DuplicateId || document.NewId == document.OldId)
                throw new InvalidOperationException("Source document does not match this account and duplicate.");
            _dbContext.CounterpartyDocuments.Remove(old);
            await _dbContext.SaveChangesAsync(cancellationToken); // cascades old additional data before new keys are inserted
            _dbContext.CounterpartyDocuments.Add(new CounterpartyDocument { AccountId = accountId,
                CounterpartyId = mainId, DocumentType = "salesreturn", DocumentId = document.NewId, UpdatedAt = document.UpdatedAt });
            _dbContext.DocumentAdditionalData.Add(new DocumentAdditionalData { DocumentId = document.NewId, RawJson = document.RawJson });
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SaveProgressAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
