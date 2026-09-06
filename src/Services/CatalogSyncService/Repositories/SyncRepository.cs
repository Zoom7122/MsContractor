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
    Task<(int Documents, int Counterparties)> ClearSnapshotAsync(Guid accountId, CancellationToken cancellationToken);
    Task CompleteFullAsync(Guid accountId, IReadOnlyCollection<Counterparty> rows, SyncCompletion completion, CancellationToken cancellationToken);
    Task<(int Inserted, int Updated)> CompleteIncrementalAsync(Guid accountId, IReadOnlyCollection<Counterparty> rows, SyncCompletion completion, Func<Counterparty, Counterparty, bool> applyIncoming, CancellationToken cancellationToken);
    Task FailAsync(Guid accountId, SyncRun run, InboxMessage inbox, SyncOutboxMessage outbox, CancellationToken cancellationToken);
}

public sealed class SyncRepository(CatalogSyncDbContext dbContext) : ISyncRepository
{
    public async Task<bool> HasProcessedAsync(Guid accountId, Guid messageId, string consumerName, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.InboxMessages.AnyAsync(x => x.MessageId == messageId && x.ConsumerName == consumerName, cancellationToken);
    }

    public async Task<SyncRun?> FindRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.SyncRuns.SingleOrDefaultAsync(x => x.AccountId == accountId && x.MessageId == messageId, cancellationToken);
    }

    public async Task<SyncRun> ReloadRunAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        dbContext.ChangeTracker.Clear();
        return await dbContext.SyncRuns.SingleAsync(x => x.AccountId == accountId && x.MessageId == messageId, cancellationToken);
    }

    public async Task<SyncWatermark?> FindWatermarkAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        return await dbContext.SyncWatermarks.AsNoTracking().SingleOrDefaultAsync(x => x.AccountId == accountId, cancellationToken);
    }

    public async Task SaveRunAsync(Guid accountId, SyncRun run, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        if (run.AccountId != accountId) throw new InvalidOperationException("Sync run belongs to another account.");
        if (dbContext.Entry(run).State == EntityState.Detached) dbContext.SyncRuns.Add(run);
        await SaveAsync(cancellationToken);
    }

    public async Task<(int Documents, int Counterparties)> ClearSnapshotAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var documents = await dbContext.CounterpartyDocuments.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        var counterparties = await dbContext.Counterparties.Where(x => x.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (documents, counterparties);
    }

    public async Task CompleteFullAsync(Guid accountId, IReadOnlyCollection<Counterparty> rows, SyncCompletion completion, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        EnsureAccount(accountId, rows, completion);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Counterparties.AddRange(rows);
        await SaveCompletionAsync(completion, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(int Inserted, int Updated)> CompleteIncrementalAsync(Guid accountId, IReadOnlyCollection<Counterparty> rows, SyncCompletion completion, Func<Counterparty, Counterparty, bool> applyIncoming, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        EnsureAccount(accountId, rows, completion);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var ids = rows.Select(x => x.Id).ToArray();
        var existing = await dbContext.Counterparties.Where(x => x.AccountId == accountId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var inserted = 0;
        var updated = 0;
        foreach (var row in rows)
        {
            if (!existing.TryGetValue(row.Id, out var current))
            {
                dbContext.Counterparties.Add(row);
                inserted++;
            }
            else if (applyIncoming(current, row)) updated++;
        }
        await SaveCompletionAsync(completion, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (inserted, updated);
    }

    public async Task FailAsync(Guid accountId, SyncRun run, InboxMessage inbox, SyncOutboxMessage outbox, CancellationToken cancellationToken)
    {
        await dbContext.SetTenantAsync(accountId, cancellationToken);
        if (run.AccountId != accountId) throw new InvalidOperationException("Sync run belongs to another account.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await dbContext.InboxMessages.AnyAsync(x => x.MessageId == inbox.MessageId && x.ConsumerName == inbox.ConsumerName, cancellationToken))
            dbContext.InboxMessages.Add(inbox);
        dbContext.OutboxMessages.Add(outbox);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SaveCompletionAsync(SyncCompletion completion, CancellationToken cancellationToken)
    {
        dbContext.InboxMessages.Add(completion.Inbox);
        dbContext.OutboxMessages.Add(completion.Outbox);
        var incoming = completion.Watermark;
        var watermark = await dbContext.SyncWatermarks.SingleOrDefaultAsync(x => x.AccountId == incoming.AccountId, cancellationToken);
        if (watermark is null) dbContext.SyncWatermarks.Add(incoming);
        else
        {
            watermark.Watermark = incoming.Watermark;
            watermark.LastSyncRunId = incoming.LastSyncRunId;
            watermark.UpdatedAt = incoming.UpdatedAt;
        }
        await SaveAsync(cancellationToken);
    }

    private static void EnsureAccount(Guid accountId, IEnumerable<Counterparty> rows, SyncCompletion completion)
    {
        if (completion.Run.AccountId != accountId || completion.Watermark.AccountId != accountId || rows.Any(x => x.AccountId != accountId))
            throw new InvalidOperationException("Sync snapshot belongs to another account.");
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) { throw new CatalogPersistenceException(exception); }
    }
}
