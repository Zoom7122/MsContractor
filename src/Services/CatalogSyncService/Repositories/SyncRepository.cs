using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ISyncRepository
{
    Task<bool> HasProcessedAsync(Guid accountId, Guid messageId, string consumerName, CancellationToken cancellationToken);
    Task<SyncRun?> FindRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken);
    Task<SyncRun> ReloadRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken);
    Task<SyncWatermark?> FindWatermarkAsync(Guid accountId, CancellationToken cancellationToken);
    Task SaveRunAsync(Guid accountId, SyncRun run, CancellationToken cancellationToken);
    Task<bool> TryAcquireRunLeaseAsync(Guid accountId, Guid syncRunId, Guid ownerToken, long nowTicks, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task<bool> RenewRunLeaseAsync(Guid accountId, Guid syncRunId, Guid ownerToken, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task RestartStagingAsync(Guid accountId, Guid syncRunId, Guid ownerToken, DateTimeOffset now, DateTimeOffset windowTo, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task SetTotalCountAsync(Guid accountId, Guid syncRunId, Guid ownerToken, int totalCount, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task StagePageAsync(Guid accountId, Guid syncRunId, Guid ownerToken, IReadOnlyCollection<CounterpartySyncStage> rows, bool incremental, DateTimeOffset now, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetArchivedStagedCounterpartyIdsAsync(Guid accountId, Guid syncRunId, bool incremental, CancellationToken cancellationToken);
    Task<int> GetStagedProcessedCountAsync(Guid accountId, Guid syncRunId, bool incremental, CancellationToken cancellationToken);
    Task ValidateFullStagingAsync(Guid accountId, Guid syncRunId, int expectedCount, CancellationToken cancellationToken);
    Task CompleteFullFromStagingAsync(Guid accountId, Guid syncRunId, Guid ownerToken, IReadOnlyCollection<CounterpartyDocument> documents, IReadOnlyCollection<DocumentAdditionalCommission> commissions, SyncCompletion completion, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task<(int Inserted, int Updated)> CompleteIncrementalFromStagingAsync(Guid accountId, Guid syncRunId, Guid ownerToken, IReadOnlyCollection<CounterpartyDocument> documents, IReadOnlyCollection<DocumentAdditionalCommission> commissions, SyncCompletion completion, Func<Counterparty, Counterparty, bool> applyIncoming, long leaseExpiresAtTicks, CancellationToken cancellationToken);
    Task<(int Documents, int Counterparties)> ClearSnapshotAsync(Guid accountId, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid accountId, Guid syncRunId, Guid ownerToken, string errorCode, string errorMessage, DateTimeOffset completedAt, InboxMessage inbox, SyncOutboxMessage outbox, CancellationToken cancellationToken);
}

public sealed class SyncRepository : ISyncRepository
{
    private const int PublishBatchSize = 500;
    private readonly CatalogSyncDbContext _dbContext;

    public SyncRepository(
        CatalogSyncDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> HasProcessedAsync(Guid accountId, Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.ConsumerName == consumerName, cancellationToken);
    }

    public async Task<SyncRun?> FindRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.SyncRuns.SingleOrDefaultAsync(x => x.AccountId == accountId && x.MessageId == messageId, cancellationToken);
    }

    public async Task<SyncRun> ReloadRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        _dbContext.ChangeTracker.Clear();
        return await _dbContext.SyncRuns.SingleAsync(x => x.AccountId == accountId && x.MessageId == messageId, cancellationToken);
    }

    public async Task<SyncWatermark?> FindWatermarkAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.SyncWatermarks.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
    }

    public async Task SaveRunAsync(Guid accountId, SyncRun run, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (run.AccountId != accountId) throw new InvalidOperationException("Sync run belongs to another account.");
        if (_dbContext.Entry(run).State == EntityState.Detached) _dbContext.SyncRuns.Add(run);
        await SaveAsync(cancellationToken);
    }

    public async Task<bool> TryAcquireRunLeaseAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        long nowTicks,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var acquired = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                (item.ProcessingOwnerToken == null || item.ProcessingLeaseExpiresAtTicks == null ||
                 item.ProcessingLeaseExpiresAtTicks <= nowTicks))
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessingOwnerToken, ownerToken)
                .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);
        return acquired == 1;
    }

    public async Task<bool> RenewRunLeaseAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var updated = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);
        return updated == 1;
    }

    public async Task RestartStagingAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        DateTimeOffset now,
        DateTimeOffset windowTo,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var ownsRun = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.TotalCount, 0)
                .SetProperty(item => item.ProcessedCount, 0)
                .SetProperty(item => item.StartedAt, item => item.StartedAt ?? now)
                .SetProperty(item => item.WindowTo, item => item.WindowTo ?? windowTo)
                .SetProperty(item => item.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(item => item.ErrorCode, (string?)null)
                .SetProperty(item => item.ErrorMessage, (string?)null)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);
        if (ownsRun != 1) throw new SyncRunLeaseLostException("The sync run lease was lost before staging could restart.");
        await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetTotalCountAsync(Guid accountId, Guid syncRunId, Guid ownerToken, int totalCount, long leaseExpiresAtTicks, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (totalCount < 0) throw new ArgumentOutOfRangeException(nameof(totalCount));
        var updated = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.TotalCount, totalCount)
                .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);
        if (updated != 1) throw new SyncRunLeaseLostException("The sync run lease was lost before the total count was saved.");
    }

    public async Task StagePageAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        IReadOnlyCollection<CounterpartySyncStage> rows,
        bool incremental,
        DateTimeOffset now,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        if (rows.Any(item => item.AccountId != accountId || item.SyncRunId != syncRunId || item.Sequence < 0))
            throw new InvalidOperationException("Counterparty staging rows must belong to the active account and run.");

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        _dbContext.CounterpartySyncStaging.AddRange(rows);
        await SaveAsync(cancellationToken);
        foreach (var row in rows)
            _dbContext.Entry(row).State = EntityState.Detached;
        var processedCount = await GetStagedProcessedCountCoreAsync(accountId, syncRunId, incremental, cancellationToken);
        var updated = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ProcessedCount, processedCount)
                .SetProperty(item => item.UpdatedAt, now)
                .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);
        if (updated != 1) throw new SyncRunLeaseLostException("The sync run lease was lost while a page was being staged.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetArchivedStagedCounterpartyIdsAsync(
        Guid accountId,
        Guid syncRunId,
        bool incremental,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var query = incremental
            ? LatestStageRows(accountId, syncRunId)
            : _dbContext.CounterpartySyncStaging.AsNoTracking()
                .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId);
        return await query
            .Where(item => item.IsValidForStorage && item.Archived)
            .Select(item => item.CounterpartyId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetStagedProcessedCountAsync(
        Guid accountId,
        Guid syncRunId,
        bool incremental,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await GetStagedProcessedCountCoreAsync(accountId, syncRunId, incremental, cancellationToken);
    }

    public Task ValidateFullStagingAsync(
        Guid accountId,
        Guid syncRunId,
        int expectedCount,
        CancellationToken cancellationToken) =>
        ValidateFullStageAsync(accountId, syncRunId, expectedCount, cancellationToken);

    public async Task CompleteFullFromStagingAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        IReadOnlyCollection<CounterpartyDocument> documents,
        IReadOnlyCollection<DocumentAdditionalCommission> commissions,
        SyncCompletion completion,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        ValidateCompletion(accountId, syncRunId, documents, commissions, completion);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await RenewOwnedLeaseCoreAsync(accountId, syncRunId, ownerToken, leaseExpiresAtTicks, cancellationToken) != 1)
            throw new SyncRunLeaseLostException("The sync run lease was lost before publication.");
        await ValidateFullStageAsync(accountId, syncRunId, completion.Run.TotalCount, cancellationToken);
        await ValidateDocumentsBelongToStageAsync(accountId, syncRunId, documents, false, cancellationToken);

        DetachAccountSnapshot(accountId);
        await _dbContext.CounterpartyDocuments.Where(item => item.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        await _dbContext.Counterparties.Where(item => item.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);

        long lastSequence = -1;
        while (true)
        {
            var page = await _dbContext.CounterpartySyncStaging.AsNoTracking()
                .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId &&
                               item.IsValidForStorage && item.Sequence > lastSequence)
                .OrderBy(item => item.Sequence)
                .Take(PublishBatchSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            var counterparties = page.Select(ToCounterparty).ToArray();
            _dbContext.Counterparties.AddRange(counterparties);
            await SaveAsync(cancellationToken);
            foreach (var counterparty in counterparties)
                _dbContext.Entry(counterparty).State = EntityState.Detached;
            lastSequence = page[^1].Sequence;
        }

        _dbContext.CounterpartyDocuments.AddRange(documents);
        _dbContext.DocumentAdditionalCommissions.AddRange(commissions);
        await SaveCompletionAsync(completion, cancellationToken);
        await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(int Inserted, int Updated)> CompleteIncrementalFromStagingAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        IReadOnlyCollection<CounterpartyDocument> documents,
        IReadOnlyCollection<DocumentAdditionalCommission> commissions,
        SyncCompletion completion,
        Func<Counterparty, Counterparty, bool> applyIncoming,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        ValidateCompletion(accountId, syncRunId, documents, commissions, completion);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await RenewOwnedLeaseCoreAsync(accountId, syncRunId, ownerToken, leaseExpiresAtTicks, cancellationToken) != 1)
            throw new SyncRunLeaseLostException("The sync run lease was lost before publication.");
        await ValidateStageCountAsync(accountId, syncRunId, completion.Run.TotalCount, cancellationToken);
        await ValidateDocumentsBelongToStageAsync(accountId, syncRunId, documents, true, cancellationToken);

        var inserted = 0;
        var updated = 0;
        long lastSequence = -1;
        var latestValidRows = LatestStageRows(accountId, syncRunId).Where(item => item.IsValidForStorage);
        while (true)
        {
            var page = await latestValidRows
                .Where(item => item.Sequence > lastSequence)
                .OrderBy(item => item.Sequence)
                .Take(PublishBatchSize)
                .ToListAsync(cancellationToken);
            if (page.Count == 0) break;

            var incoming = page.Select(ToCounterparty).ToArray();
            var ids = incoming.Select(item => item.Id).ToArray();
            var existing = await _dbContext.Counterparties
                .Where(item => item.AccountId == accountId && ids.Contains(item.Id))
                .ToDictionaryAsync(item => item.Id, cancellationToken);
            var refreshedArchivedIds = new HashSet<Guid>();
            var touchedCounterparties = new List<Counterparty>();
            foreach (var row in incoming)
            {
                if (!existing.TryGetValue(row.Id, out var current))
                {
                    _dbContext.Counterparties.Add(row);
                    touchedCounterparties.Add(row);
                    inserted++;
                    if (row.Archived) refreshedArchivedIds.Add(row.Id);
                }
                else if (applyIncoming(current, row))
                {
                    touchedCounterparties.Add(current);
                    updated++;
                    if (row.Archived) refreshedArchivedIds.Add(row.Id);
                }
            }

            await SaveAsync(cancellationToken);
            foreach (var counterparty in incoming)
            {
                var entry = _dbContext.Entry(counterparty);
                if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
            }
            foreach (var counterparty in existing.Values)
            {
                var entry = _dbContext.Entry(counterparty);
                if (entry.State != EntityState.Detached) entry.State = EntityState.Detached;
            }

            if (refreshedArchivedIds.Count > 0)
                await ReplaceArchivedDocumentsAsync(
                    accountId, refreshedArchivedIds, documents, commissions, cancellationToken);
            lastSequence = page[^1].Sequence;
        }

        await SaveCompletionAsync(completion, cancellationToken);
        await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId)
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (inserted, updated);
    }

    public async Task<(int Documents, int Counterparties)> ClearSnapshotAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var documents = await _dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        var counterparties = await _dbContext.Counterparties.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (documents, counterparties);
    }

    public async Task<bool> FailAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        string errorCode,
        string errorMessage,
        DateTimeOffset completedAt,
        InboxMessage inbox,
        SyncOutboxMessage outbox,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var failed = await _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.ErrorCode, errorCode)
                .SetProperty(item => item.ErrorMessage, errorMessage)
                .SetProperty(item => item.CompletedAt, completedAt)
                .SetProperty(item => item.UpdatedAt, completedAt)
                .SetProperty(item => item.ProcessingOwnerToken, (Guid?)null)
                .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, (long?)null), cancellationToken);
        if (failed != 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId)
            .ExecuteDeleteAsync(cancellationToken);
        if (!await _dbContext.InboxMessages.AnyAsync(x => x.MessageId == inbox.MessageId && x.ConsumerName == inbox.ConsumerName, cancellationToken))
            _dbContext.InboxMessages.Add(inbox);
        _dbContext.OutboxMessages.Add(outbox);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task SaveCompletionAsync(SyncCompletion completion, CancellationToken cancellationToken)
    {
        _dbContext.InboxMessages.Add(completion.Inbox);
        _dbContext.OutboxMessages.Add(completion.Outbox);
        var incoming = completion.Watermark;
        var watermark = await _dbContext.SyncWatermarks.SingleOrDefaultAsync(x => x.AccountId == incoming.AccountId, cancellationToken);
        if (watermark is null) _dbContext.SyncWatermarks.Add(incoming);
        else
        {
            watermark.Watermark = incoming.Watermark;
            watermark.LastSyncRunId = incoming.LastSyncRunId;
            watermark.UpdatedAt = incoming.UpdatedAt;
        }
        await SaveAsync(cancellationToken);
    }

    private async Task<int> GetStagedProcessedCountCoreAsync(
        Guid accountId,
        Guid syncRunId,
        bool incremental,
        CancellationToken cancellationToken)
    {
        var query = incremental
            ? LatestStageRows(accountId, syncRunId)
            : _dbContext.CounterpartySyncStaging.AsNoTracking()
                .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId);
        return await query
            .Where(item => item.IsValidForStorage)
            .Select(item => item.CounterpartyId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private Task<int> RenewOwnedLeaseCoreAsync(
        Guid accountId,
        Guid syncRunId,
        Guid ownerToken,
        long leaseExpiresAtTicks,
        CancellationToken cancellationToken) =>
        _dbContext.SyncRuns
            .Where(item => item.AccountId == accountId && item.Id == syncRunId && item.Status == "running" &&
                           item.ProcessingOwnerToken == ownerToken)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.ProcessingLeaseExpiresAtTicks, leaseExpiresAtTicks), cancellationToken);

    private IQueryable<CounterpartySyncStage> LatestStageRows(Guid accountId, Guid syncRunId)
    {
        var allRows = _dbContext.CounterpartySyncStaging.AsNoTracking();
        return allRows.Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId &&
            !allRows.Any(other => other.AccountId == accountId && other.SyncRunId == syncRunId &&
                other.CounterpartyId == item.CounterpartyId &&
                (other.MoySkladUpdatedSortValue > item.MoySkladUpdatedSortValue ||
                 (other.MoySkladUpdatedSortValue == item.MoySkladUpdatedSortValue && other.Sequence > item.Sequence))));
    }

    private async Task ValidateStageCountAsync(
        Guid accountId,
        Guid syncRunId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var stagedCount = await _dbContext.CounterpartySyncStaging
            .CountAsync(item => item.AccountId == accountId && item.SyncRunId == syncRunId, cancellationToken);
        if (stagedCount != expectedCount)
            throw new CounterpartySnapshotChangedException("The staged counterparty count does not match the Egress snapshot.");
    }

    private async Task ValidateFullStageAsync(
        Guid accountId,
        Guid syncRunId,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await ValidateStageCountAsync(accountId, syncRunId, expectedCount, cancellationToken);
        var distinctCount = await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == accountId && item.SyncRunId == syncRunId)
            .Select(item => item.CounterpartyId)
            .Distinct()
            .CountAsync(cancellationToken);
        if (distinctCount != expectedCount)
            throw new CounterpartySnapshotChangedException("The Egress counterparty snapshot contains duplicate IDs.");
    }

    private void ValidateCompletion(
        Guid accountId,
        Guid syncRunId,
        IReadOnlyCollection<CounterpartyDocument> documents,
        IReadOnlyCollection<DocumentAdditionalCommission> commissions,
        SyncCompletion completion)
    {
        var documentIds = documents.Select(item => item.DocumentId).ToHashSet();
        if (completion.Run.AccountId != accountId || completion.Run.Id != syncRunId ||
            completion.Watermark.AccountId != accountId ||
            completion.Watermark.LastSyncRunId != syncRunId ||
            documents.Any(item => item.AccountId != accountId || item.DocumentId == Guid.Empty) ||
            documentIds.Count != documents.Count ||
            commissions.Any(item => !documentIds.Contains(item.DocumentId)) ||
            commissions.Select(item => item.DocumentId).Distinct().Count() != commissions.Count)
            throw new InvalidOperationException("Sync completion data belongs to another account or run.");
    }

    private async Task ValidateDocumentsBelongToStageAsync(
        Guid accountId,
        Guid syncRunId,
        IReadOnlyCollection<CounterpartyDocument> documents,
        bool incremental,
        CancellationToken cancellationToken)
    {
        var archivedIds = await GetArchivedStagedCounterpartyIdsAsync(
            accountId, syncRunId, incremental, cancellationToken);
        var archivedIdSet = archivedIds.ToHashSet();
        if (documents.Any(item => !archivedIdSet.Contains(item.CounterpartyId)))
            throw new InvalidOperationException("Archived document snapshot does not match staged counterparties.");
    }

    private async Task ReplaceArchivedDocumentsAsync(
        Guid accountId,
        IReadOnlySet<Guid> counterpartyIds,
        IReadOnlyCollection<CounterpartyDocument> documents,
        IReadOnlyCollection<DocumentAdditionalCommission> commissions,
        CancellationToken cancellationToken)
    {
        var oldDocumentIds = await _dbContext.CounterpartyDocuments.AsNoTracking()
            .Where(item => item.AccountId == accountId && counterpartyIds.Contains(item.CounterpartyId))
            .Select(item => item.DocumentId)
            .ToArrayAsync(cancellationToken);
        foreach (var entry in _dbContext.ChangeTracker.Entries<DocumentAdditionalCommission>()
                     .Where(entry => oldDocumentIds.Contains(entry.Entity.DocumentId)).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in _dbContext.ChangeTracker.Entries<CounterpartyDocument>()
                     .Where(entry => entry.Entity.AccountId == accountId && counterpartyIds.Contains(entry.Entity.CounterpartyId)).ToArray())
            entry.State = EntityState.Detached;

        await _dbContext.CounterpartyDocuments
            .Where(item => item.AccountId == accountId && counterpartyIds.Contains(item.CounterpartyId))
            .ExecuteDeleteAsync(cancellationToken);
        var replacementDocuments = documents.Where(item => counterpartyIds.Contains(item.CounterpartyId)).ToArray();
        var replacementDocumentIds = replacementDocuments.Select(item => item.DocumentId).ToHashSet();
        _dbContext.CounterpartyDocuments.AddRange(replacementDocuments);
        _dbContext.DocumentAdditionalCommissions.AddRange(
            commissions.Where(item => replacementDocumentIds.Contains(item.DocumentId)));
        await SaveAsync(cancellationToken);
    }

    private static Counterparty ToCounterparty(CounterpartySyncStage item) => new()
    {
        Id = item.CounterpartyId,
        AccountId = item.AccountId,
        Name = item.Name,
        Phone = item.Phone,
        Email = item.Email,
        Inn = item.Inn,
        Kpp = item.Kpp,
        Description = item.Description,
        Archived = item.Archived,
        NormalizedName = item.NormalizedName,
        NormalizedPhone = item.NormalizedPhone,
        NormalizedEmail = item.NormalizedEmail,
        NormalizedInn = item.NormalizedInn,
        NormalizedKpp = item.NormalizedKpp,
        MoySkladUpdatedAt = item.MoySkladUpdatedAt,
        LastSyncRunId = item.LastSyncRunId,
        RawJson = item.RawJson,
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt
    };

    private void DetachAccountSnapshot(Guid accountId)
    {
        var trackedDocuments = _dbContext.ChangeTracker.Entries<CounterpartyDocument>()
            .Where(entry => entry.Entity.AccountId == accountId).ToArray();
        var documentIds = trackedDocuments.Select(entry => entry.Entity.DocumentId).ToHashSet();
        foreach (var entry in _dbContext.ChangeTracker.Entries<DocumentAdditionalCommission>()
                     .Where(entry => documentIds.Contains(entry.Entity.DocumentId)).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in trackedDocuments)
            entry.State = EntityState.Detached;
        foreach (var entry in _dbContext.ChangeTracker.Entries<Counterparty>()
                     .Where(entry => entry.Entity.AccountId == accountId).ToArray())
            entry.State = EntityState.Detached;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await _dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new CatalogPersistenceException(exception); }
    }
}
