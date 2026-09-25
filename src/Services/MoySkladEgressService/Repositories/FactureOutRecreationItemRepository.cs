using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface IFactureOutRecreationItemRepository
{
    Task UpsertPreparationAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid?> preparedSourceSyncIds,
        IReadOnlyCollection<Guid> skippedDocumentIds,
        CancellationToken cancellationToken);

    Task SavePayloadBuildResultsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid> newSyncIds,
        IReadOnlyCollection<Guid> skippedDocumentIds,
        CancellationToken cancellationToken);

    Task SaveRecreationResultsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid> transferredDocumentIds,
        IReadOnlyCollection<Guid> failedDocumentIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutRecreationItemRepository : IFactureOutRecreationItemRepository
{
    private readonly EgressDbContext _dbContext;

    public FactureOutRecreationItemRepository(EgressDbContext dbContext) => _dbContext = dbContext;

    public async Task UpsertPreparationAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid?> preparedSourceSyncIds,
        IReadOnlyCollection<Guid> skippedDocumentIds,
        CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        ValidateDocumentIds(preparedSourceSyncIds.Keys, nameof(preparedSourceSyncIds));
        ValidateDocumentIds(skippedDocumentIds, nameof(skippedDocumentIds));

        var preparedIds = preparedSourceSyncIds.Keys.ToHashSet();
        if (skippedDocumentIds.Any(preparedIds.Contains))
        {
            throw new ArgumentException(
                "A document cannot be both prepared and skipped.",
                nameof(skippedDocumentIds));
        }

        var allIds = preparedIds.Concat(skippedDocumentIds).ToArray();
        if (allIds.Length == 0)
            return;

        var existing = await _dbContext.FactureOutRecreationItems
            .Where(item => item.AccountId == accountId && allIds.Contains(item.SourceDocumentId))
            .ToDictionaryAsync(item => item.SourceDocumentId, cancellationToken);

        foreach (var (documentId, sourceSyncId) in preparedSourceSyncIds)
        {
            var item = GetOrCreate(accountId, documentId, existing);
            item.SourceSyncId = sourceSyncId;
            item.NewSyncId = null;
            item.NewDocumentId = null;
            item.Status = FactureOutRecreationStatuses.Prepared;
        }

        foreach (var documentId in skippedDocumentIds)
        {
            var item = GetOrCreate(accountId, documentId, existing);
            item.SourceSyncId = null;
            item.NewSyncId = null;
            item.NewDocumentId = null;
            item.Status = FactureOutRecreationStatuses.Skipped;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SavePayloadBuildResultsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid> newSyncIds,
        IReadOnlyCollection<Guid> skippedDocumentIds,
        CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        ValidateDocumentIds(newSyncIds.Keys, nameof(newSyncIds));
        ValidateDocumentIds(skippedDocumentIds, nameof(skippedDocumentIds));
        if (newSyncIds.Values.Any(syncId => syncId == Guid.Empty))
            throw new ArgumentException("New sync ids must be non-empty.", nameof(newSyncIds));

        var successfulIds = newSyncIds.Keys.ToHashSet();
        if (skippedDocumentIds.Any(successfulIds.Contains))
        {
            throw new ArgumentException(
                "A document cannot have both a generated payload and a skipped status.",
                nameof(skippedDocumentIds));
        }

        var allIds = successfulIds.Concat(skippedDocumentIds).ToArray();
        if (allIds.Length == 0)
            return;

        var items = await _dbContext.FactureOutRecreationItems
            .Where(item => item.AccountId == accountId && allIds.Contains(item.SourceDocumentId))
            .ToDictionaryAsync(item => item.SourceDocumentId, cancellationToken);

        if (items.Count != allIds.Length)
        {
            var missingId = allIds.First(id => !items.ContainsKey(id));
            throw new InvalidOperationException(
                $"Factureout recreation item {missingId:D} was not prepared for account {accountId:D}.");
        }

        foreach (var (documentId, newSyncId) in newSyncIds)
        {
            var item = items[documentId];
            item.NewSyncId = newSyncId;
            item.NewDocumentId = null;
            item.Status = FactureOutRecreationStatuses.CreatePayload;
        }

        foreach (var documentId in skippedDocumentIds)
        {
            var item = items[documentId];
            item.NewSyncId = null;
            item.NewDocumentId = null;
            item.Status = FactureOutRecreationStatuses.Skipped;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveRecreationResultsAsync(
        Guid accountId,
        IReadOnlyDictionary<Guid, Guid> transferredDocumentIds,
        IReadOnlyCollection<Guid> failedDocumentIds,
        CancellationToken cancellationToken)
    {
        ValidateAccountId(accountId);
        ValidateDocumentIds(transferredDocumentIds.Keys, nameof(transferredDocumentIds));
        ValidateDocumentIds(failedDocumentIds, nameof(failedDocumentIds));

        var transferredIds = transferredDocumentIds.Keys.ToHashSet();
        if (transferredDocumentIds.Values.Any(id => id == Guid.Empty) ||
            failedDocumentIds.Any(transferredIds.Contains))
        {
            throw new ArgumentException("Recreation results must contain distinct, non-empty document ids.");
        }

        var allIds = transferredIds.Concat(failedDocumentIds).ToArray();
        if (allIds.Length == 0)
            return;

        var items = await _dbContext.FactureOutRecreationItems
            .Where(item => item.AccountId == accountId && allIds.Contains(item.SourceDocumentId))
            .ToDictionaryAsync(item => item.SourceDocumentId, cancellationToken);

        if (items.Count != allIds.Length)
        {
            var missingId = allIds.First(id => !items.ContainsKey(id));
            throw new InvalidOperationException(
                $"Factureout recreation item {missingId:D} was not prepared for account {accountId:D}.");
        }

        foreach (var (sourceDocumentId, newDocumentId) in transferredDocumentIds)
        {
            var item = items[sourceDocumentId];
            item.NewDocumentId = newDocumentId;
            item.Status = FactureOutRecreationStatuses.Created;
        }

        foreach (var sourceDocumentId in failedDocumentIds)
        {
            var item = items[sourceDocumentId];
            item.NewDocumentId = null;
            item.Status = FactureOutRecreationStatuses.Failed;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private FactureOutRecreationItem GetOrCreate(
        Guid accountId,
        Guid sourceDocumentId,
        IDictionary<Guid, FactureOutRecreationItem> existing)
    {
        if (existing.TryGetValue(sourceDocumentId, out var item))
            return item;

        item = new FactureOutRecreationItem
        {
            AccountId = accountId,
            SourceDocumentId = sourceDocumentId
        };
        _dbContext.FactureOutRecreationItems.Add(item);
        existing.Add(sourceDocumentId, item);
        return item;
    }

    private static void ValidateAccountId(Guid accountId)
    {
        if (accountId == Guid.Empty)
            throw new ArgumentException("A non-empty account id is required.", nameof(accountId));
    }

    private static void ValidateDocumentIds(IEnumerable<Guid> documentIds, string parameterName)
    {
        var ids = documentIds.ToArray();
        if (ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length)
        {
            throw new ArgumentException(
                "Factureout document ids must be non-empty and unique.",
                parameterName);
        }
    }
}
