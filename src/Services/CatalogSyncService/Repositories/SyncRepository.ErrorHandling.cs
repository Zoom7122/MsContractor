using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;
using Npgsql;

namespace MsContractor.CatalogSyncService.Repositories;

public sealed partial class SyncRepository
{
    // Only uncommitted diagnostics are buffered. PostgreSQL owns the committed journal.
    private readonly Dictionary<(Guid AccountId, Guid RunId, string Key), SyncRunError> _pendingErrors = new();

    private void RememberErrors(Guid accountId, Guid runId, IEnumerable<SyncRunError> errors)
    {
        foreach (var error in errors)
        {
            if (error.AccountId != accountId || error.SyncRunId != runId || error.CounterpartyId == Guid.Empty)
                throw new InvalidOperationException("Sync errors must belong to the active account and run.");
            _pendingErrors.TryAdd((accountId, runId, error.ErrorKey), error);
        }
    }

    private async Task FlushErrorsCoreAsync(Guid accountId, Guid runId, CancellationToken cancellationToken)
    {
        var pending = _pendingErrors.Values
            .Where(error => error.AccountId == accountId && error.SyncRunId == runId).ToArray();
        if (pending.Length == 0) return;
        var keys = pending.Select(error => error.ErrorKey).ToArray();
        var existing = await _dbContext.SyncRunErrors.AsNoTracking()
            .Where(error => error.AccountId == accountId && error.SyncRunId == runId && keys.Contains(error.ErrorKey))
            .Select(error => error.ErrorKey).ToHashSetAsync(cancellationToken);
        var added = pending.Where(error => !existing.Contains(error.ErrorKey)).ToArray();
        if (added.Length == 0) return;

        // The caller holds the run row lock after its owner-token check. This serializes
        // deduplication and the counter increment with completion and lease takeover.
        _dbContext.SyncRunErrors.AddRange(added);
        await SaveAsync(cancellationToken);
        var updated = await _dbContext.SyncRuns
            .Where(run => run.AccountId == accountId && run.Id == runId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.ErrorsCount,
                run => run.ErrorsCount + added.Length), cancellationToken);
        if (updated != 1) throw new SyncRunLeaseLostException("The sync run disappeared while errors were saved.");
        foreach (var error in added) _dbContext.Entry(error).State = EntityState.Detached;
    }

    private void ForgetCommittedErrors(Guid accountId, Guid runId)
    {
        foreach (var key in _pendingErrors.Keys.Where(key => key.AccountId == accountId && key.RunId == runId).ToArray())
            _pendingErrors.Remove(key);
    }

    private void DetachRun(Guid accountId, Guid runId)
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries<SyncRun>()
                     .Where(entry => entry.Entity.AccountId == accountId && entry.Entity.Id == runId).ToArray())
            entry.State = EntityState.Detached;
    }

    private void DetachCounterpartyWrites()
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries()
                     .Where(entry => entry.Entity is Counterparty or CounterpartySyncStage).ToArray())
            entry.State = EntityState.Detached;
    }

    private static PostgresException? RecoverableRowError(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is PostgresException { SqlState: "22001" or "22021" or "22P02" or "22P05" } postgres)
                return postgres;
        return null;
    }

    private async Task SaveWithIsolationAsync<T>(
        IReadOnlyList<T> rows,
        Func<IReadOnlyList<T>, CancellationToken, Task> save,
        Func<T, PostgresException, CancellationToken, Task> reject,
        CancellationToken cancellationToken)
    {
        var transaction = _dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Counterparty isolation requires a repository transaction.");
        await transaction.CreateSavepointAsync("sync_batch", cancellationToken);
        try
        {
            await save(rows, cancellationToken);
            DetachCounterpartyWrites();
            await transaction.ReleaseSavepointAsync("sync_batch", cancellationToken);
            return;
        }
        catch (CatalogPersistenceException exception) when (RecoverableRowError(exception) is not null)
        {
            await transaction.RollbackToSavepointAsync("sync_batch", cancellationToken);
            DetachCounterpartyWrites();
            await transaction.ReleaseSavepointAsync("sync_batch", cancellationToken);
        }

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CreateSavepointAsync("sync_row", cancellationToken);
            try
            {
                await save([row], cancellationToken);
            }
            catch (CatalogPersistenceException exception) when (RecoverableRowError(exception) is not null)
            {
                await transaction.RollbackToSavepointAsync("sync_row", cancellationToken);
                DetachCounterpartyWrites();
                await reject(row, RecoverableRowError(exception)!, cancellationToken);
            }
            DetachCounterpartyWrites();
            await transaction.ReleaseSavepointAsync("sync_row", cancellationToken);
        }
    }

    private void RememberDataError(CounterpartySyncStage row, PostgresException error, string stage)
    {
        var message = error.SqlState == "22001"
            ? "Поле контрагента превышает допустимую длину при сохранении в БД."
            : "Не удалось сохранить контрагента: недопустимые символы или формат значения.";
        RememberErrors(row.AccountId, row.SyncRunId,
            [SyncRunError.Create(row.AccountId, row.SyncRunId, row.CounterpartyId,
                "COUNTERPARTY_DATA_INVALID", message, row.UpdatedAt, $"{stage}:{error.SqlState}")]);
    }

    private async Task RejectStagingRowAsync(CounterpartySyncStage row, PostgresException error, CancellationToken cancellationToken)
    {
        RememberDataError(row, error, "staging");
        _dbContext.CounterpartySyncStaging.Add(new CounterpartySyncStage
        {
            AccountId = row.AccountId, SyncRunId = row.SyncRunId, Sequence = row.Sequence,
            CounterpartyId = row.CounterpartyId, IsValidForStorage = false,
            Name = string.Empty, NormalizedName = string.Empty, RawJson = "{}", Archived = row.Archived,
            MoySkladUpdatedAt = row.MoySkladUpdatedAt, MoySkladUpdatedSortValue = row.MoySkladUpdatedSortValue,
            LastSyncRunId = row.LastSyncRunId, CreatedAt = row.CreatedAt, UpdatedAt = row.UpdatedAt
        });
        await SaveAsync(cancellationToken);
    }

    private async Task RejectPublicationRowAsync(CounterpartySyncStage row, PostgresException error, CancellationToken cancellationToken)
    {
        RememberDataError(row, error, "publication");
        var updated = await _dbContext.CounterpartySyncStaging
            .Where(item => item.AccountId == row.AccountId && item.SyncRunId == row.SyncRunId &&
                           item.Sequence == row.Sequence && item.CounterpartyId == row.CounterpartyId)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.IsValidForStorage, false), cancellationToken);
        if (updated != 1) throw new InvalidOperationException("Rejected counterparty is missing from staging.");
    }
}
