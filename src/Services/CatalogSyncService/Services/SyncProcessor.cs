using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Sync;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncProcessor
{
    Task ProcessAsync(SyncRequested command, CancellationToken cancellationToken);
}

public sealed class CounterpartySnapshotChangedException(string message) : Exception(message);

public sealed class SyncProcessor(
    CatalogSyncDbContext dbContext,
    IMoySkladEgressClient egressClient,
    IMoySkladCounterpartyParser parser,
    ICounterpartyNormalizer normalizer,
    TimeProvider timeProvider,
    ILogger<SyncProcessor> logger) : ISyncProcessor
{
    public const string ConsumerName = "catalog-sync-counterparties-v1";
    private const int PageSize = 1000;
    private static readonly TimeSpan SafetyOverlap = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(
        SyncRequested command,
        CancellationToken cancellationToken)
    {
        Validate(command);
        if (await dbContext.InboxMessages.AnyAsync(
                item => item.MessageId == command.MessageId &&
                        item.ConsumerName == ConsumerName,
                cancellationToken))
        {
            logger.LogInformation(
                "Sync message already processed: message_id={MessageId}, account_id={AccountId}, sync_run_id={SyncRunId}",
                command.MessageId,
                command.AccountId,
                command.SyncRunId);
            return;
        }

        var run = await EnsureRunningAsync(command, cancellationToken);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["account_id"] = command.AccountId,
            ["sync_run_id"] = command.SyncRunId,
            ["message_id"] = command.MessageId,
            ["requested_by_user_id"] = command.RequestedByUserId,
            ["correlation_id"] = command.MessageId,
            ["requested_mode"] = run.RequestedMode,
            ["execution_mode"] = run.ExecutionMode,
            ["window_from"] = run.WindowFrom,
            ["window_to"] = run.WindowTo
        });

        try
        {
            if (run.ExecutionMode == ModeName(SyncMode.Incremental))
                await ProcessIncrementalAsync(command, run, cancellationToken);
            else
                await ProcessFullAsync(command, run, cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(command, exception, cancellationToken);
        }
    }

    private async Task ProcessFullAsync(
        SyncRequested command,
        SyncRun run,
        CancellationToken cancellationToken)
    {
        // A full synchronization starts from an empty account-scoped catalog.
        // This delete is intentionally committed before any MoySklad requests.
        var deletedCount = await dbContext.Counterparties
            .Where(item => item.AccountId == command.AccountId)
            .ExecuteDeleteAsync(cancellationToken);
        logger.LogInformation(
            "Existing counterparties cleared before full sync: deleted_count={DeletedCount}",
            deletedCount);

        var snapshot = await LoadSnapshotAsync(command, null, null, cancellationToken);
        if (snapshot.Rows.Select(item => item.Value.Id).Distinct().Count() != snapshot.TotalCount)
            throw SnapshotChanged();

        var now = timeProvider.GetUtcNow();
        var counterparties = snapshot.Rows
            .Select(item => normalizer.Create(command.AccountId, command.SyncRunId, item, now))
            .Where(IsValidForStorage)
            .ToArray();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Counterparties.AddRange(counterparties);
        await CompleteAsync(command, run, snapshot.TotalCount, counterparties.Length, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Full counterparty sync completed: deleted_count={DeletedCount}, processed_count={ProcessedCount}, total_count={TotalCount}, active_count={ActiveCount}, archived_count={ArchivedCount}, window_to={WindowTo}",
            deletedCount,
            counterparties.Length,
            snapshot.TotalCount,
            snapshot.ActiveCount,
            snapshot.ArchivedCount,
            run.WindowTo);
    }

    private async Task ProcessIncrementalAsync(
        SyncRequested command,
        SyncRun run,
        CancellationToken cancellationToken)
    {
        if (run.WindowFrom is null || run.WindowTo is null || run.WindowFrom >= run.WindowTo)
            throw new InvalidOperationException("Incremental sync window is invalid.");

        var snapshot = await LoadSnapshotAsync(
            command,
            run.WindowFrom,
            run.WindowTo,
            cancellationToken);
        var uniqueRows = snapshot.Rows
            .Select((row, index) => new { Row = row, Index = index })
            .GroupBy(item => item.Row.Value.Id)
            .Select(group => group
                .OrderBy(item => item.Row.Value.Updated)
                .ThenBy(item => item.Index)
                .Last().Row)
            .ToArray();
        var now = timeProvider.GetUtcNow();
        var incoming = uniqueRows
            .Select(item => normalizer.Create(command.AccountId, command.SyncRunId, item, now))
            .Where(IsValidForStorage)
            .ToArray();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var counterpartyIds = incoming.Select(item => item.Id).ToArray();
        var existing = await dbContext.Counterparties
            .Where(item => item.AccountId == command.AccountId && counterpartyIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var insertedCount = 0;
        var updatedCount = 0;
        foreach (var item in incoming)
        {
            if (!existing.TryGetValue(item.Id, out var current))
            {
                dbContext.Counterparties.Add(item);
                insertedCount++;
                continue;
            }

            if (current.MoySkladUpdatedAt is not null &&
                item.MoySkladUpdatedAt is not null &&
                item.MoySkladUpdatedAt < current.MoySkladUpdatedAt)
            {
                continue;
            }

            ApplyIncoming(current, item);
            updatedCount++;
        }

        await CompleteAsync(command, run, snapshot.TotalCount, incoming.Length, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Incremental counterparty sync completed: processed_count={ProcessedCount}, total_count={TotalCount}, inserted_count={InsertedCount}, updated_count={UpdatedCount}, active_count={ActiveCount}, archived_count={ArchivedCount}, window_from={WindowFrom}, window_to={WindowTo}",
            incoming.Length,
            snapshot.TotalCount,
            insertedCount,
            updatedCount,
            snapshot.ActiveCount,
            snapshot.ArchivedCount,
            run.WindowFrom,
            run.WindowTo);
    }

    private async Task<LoadedSnapshot> LoadSnapshotAsync(
        SyncRequested command,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        var activeCount = await GetCountAsync(command, false, windowFrom, windowTo, cancellationToken);
        var archivedCount = await GetCountAsync(command, true, windowFrom, windowTo, cancellationToken);
        var totalCount = checked(activeCount + archivedCount);
        var rows = new List<ParsedCounterparty>(totalCount);
        await LoadPagesAsync(command, false, activeCount, windowFrom, windowTo, rows, cancellationToken);
        await LoadPagesAsync(command, true, archivedCount, windowFrom, windowTo, rows, cancellationToken);
        if (rows.Count != totalCount)
            throw SnapshotChanged();
        return new LoadedSnapshot(rows, activeCount, archivedCount, totalCount);
    }

    private async Task<int> GetCountAsync(
        SyncRequested command,
        bool archived,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        var parsed = await GetPageAsync(command, archived, 1, 0, windowFrom, windowTo, cancellationToken);
        if (parsed.Meta.Size < 0)
            throw SnapshotChanged();
        return parsed.Meta.Size;
    }

    private async Task LoadPagesAsync(
        SyncRequested command,
        bool archived,
        int expectedCount,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        ICollection<ParsedCounterparty> destination,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < expectedCount; offset += PageSize)
        {
            var parsed = await GetPageAsync(
                command,
                archived,
                PageSize,
                offset,
                windowFrom,
                windowTo,
                cancellationToken);
            var expectedPageCount = Math.Min(PageSize, expectedCount - offset);
            if (parsed.Meta.Size != expectedCount || parsed.Rows.Count != expectedPageCount)
                throw SnapshotChanged();

            if (windowFrom is not null && windowTo is not null && parsed.Rows.Any(item =>
                    item.Value.Updated is null ||
                    item.Value.Updated < windowFrom ||
                    item.Value.Updated >= windowTo))
            {
                throw SnapshotChanged();
            }

            foreach (var row in parsed.Rows)
                destination.Add(row);
        }
    }

    private async Task<ParsedCounterpartyCollection> GetPageAsync(
        SyncRequested command,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        var rawResponse = await egressClient.GetCounterpartiesAsync(
            command.AccountId,
            archived,
            limit,
            offset,
            windowFrom,
            windowTo,
            command.SyncRunId,
            command.RequestedByUserId,
            command.MessageId.ToString("D"),
            cancellationToken);
        return parser.Parse(rawResponse.Json);
    }

    private async Task<SyncRun> EnsureRunningAsync(
        SyncRequested command,
        CancellationToken cancellationToken)
    {
        var run = await dbContext.SyncRuns.SingleOrDefaultAsync(
            item => item.MessageId == command.MessageId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (run is null)
        {
            var requestedMode = ModeName(command.Mode);
            var watermark = command.Mode == SyncMode.Incremental
                ? await dbContext.SyncWatermarks.AsNoTracking().SingleOrDefaultAsync(
                    item => item.AccountId == command.AccountId,
                    cancellationToken)
                : null;
            var executionMode = command.Mode == SyncMode.Incremental && watermark is not null
                ? ModeName(SyncMode.Incremental)
                : ModeName(SyncMode.Full);
            var windowTo = TruncateToMilliseconds(now);
            run = new SyncRun
            {
                Id = command.SyncRunId,
                MessageId = command.MessageId,
                AccountId = command.AccountId,
                RequestedByUserId = command.RequestedByUserId,
                RequestedMode = requestedMode,
                ExecutionMode = executionMode,
                WindowFrom = executionMode == ModeName(SyncMode.Incremental)
                    ? watermark!.Watermark - SafetyOverlap
                    : null,
                WindowTo = windowTo,
                Status = "running",
                StartedAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.SyncRuns.Add(run);
        }
        else
        {
            if (run.AccountId != command.AccountId ||
                run.Id != command.SyncRunId ||
                run.RequestedByUserId != command.RequestedByUserId ||
                run.RequestedMode != ModeName(command.Mode))
            {
                throw new InvalidOperationException("MessageId is associated with another sync command.");
            }

            run.Status = "running";
            run.StartedAt ??= now;
            run.WindowTo ??= TruncateToMilliseconds(now);
            run.UpdatedAt = now;
            run.CompletedAt = null;
            run.ErrorCode = null;
            run.ErrorMessage = null;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task CompleteAsync(
        SyncRequested command,
        SyncRun run,
        int totalCount,
        int processedCount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (run.WindowTo is null)
            throw new InvalidOperationException("Sync windowTo is missing.");

        run.Status = "completed";
        run.ProcessedCount = processedCount;
        run.TotalCount = totalCount;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        dbContext.InboxMessages.Add(new InboxMessage
        {
            MessageId = command.MessageId,
            ConsumerName = ConsumerName,
            ProcessedAt = now
        });
        AddOutbox(
            new SyncCompleted(
                Guid.NewGuid(),
                command.SyncRunId,
                command.AccountId,
                processedCount,
                now),
            command.AccountId,
            now);

        var watermark = await dbContext.SyncWatermarks.SingleOrDefaultAsync(
            item => item.AccountId == command.AccountId,
            cancellationToken);
        if (watermark is null)
        {
            dbContext.SyncWatermarks.Add(new SyncWatermark
            {
                AccountId = command.AccountId,
                Watermark = run.WindowTo.Value,
                LastSyncRunId = command.SyncRunId,
                UpdatedAt = now
            });
        }
        else
        {
            watermark.Watermark = run.WindowTo.Value;
            watermark.LastSyncRunId = command.SyncRunId;
            watermark.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(
        SyncRequested command,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (code, safeMessage) = MapError(exception);
        logger.LogError(
            exception,
            "Counterparty sync failed: account_id={AccountId}, sync_run_id={SyncRunId}, message_id={MessageId}, requested_by_user_id={UserId}, error_code={ErrorCode}",
            command.AccountId,
            command.SyncRunId,
            command.MessageId,
            command.RequestedByUserId,
            code);

        dbContext.ChangeTracker.Clear();
        var run = await dbContext.SyncRuns.SingleAsync(
            item => item.MessageId == command.MessageId,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        run.Status = "failed";
        run.ErrorCode = code;
        run.ErrorMessage = safeMessage;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        if (!await dbContext.InboxMessages.AnyAsync(
                item => item.MessageId == command.MessageId &&
                        item.ConsumerName == ConsumerName,
                cancellationToken))
        {
            dbContext.InboxMessages.Add(new InboxMessage
            {
                MessageId = command.MessageId,
                ConsumerName = ConsumerName,
                ProcessedAt = now
            });
        }

        AddOutbox(
            new SyncFailed(
                Guid.NewGuid(),
                command.SyncRunId,
                command.AccountId,
                code,
                safeMessage,
                now),
            command.AccountId,
            now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void ApplyIncoming(Counterparty target, Counterparty source)
    {
        target.Name = source.Name;
        target.Phone = source.Phone;
        target.Email = source.Email;
        target.Inn = source.Inn;
        target.Kpp = source.Kpp;
        target.Description = source.Description;
        target.Archived = source.Archived;
        target.NormalizedName = source.NormalizedName;
        target.NormalizedPhone = source.NormalizedPhone;
        target.NormalizedEmail = source.NormalizedEmail;
        target.NormalizedInn = source.NormalizedInn;
        target.NormalizedKpp = source.NormalizedKpp;
        target.MoySkladUpdatedAt = source.MoySkladUpdatedAt;
        target.LastSyncRunId = source.LastSyncRunId;
        target.RawJson = source.RawJson;
        target.UpdatedAt = source.UpdatedAt;
    }

    private bool IsValidForStorage(Counterparty counterparty)
    {
        if (CounterpartyStorageValidator.TryValidate(counterparty, out var field, out var length, out var maxLength))
            return true;

        logger.LogWarning(
            "Counterparty skipped because a field exceeds its database limit: counterparty_id={CounterpartyId}, account_id={AccountId}, field={Field}, length={Length}, max_length={MaxLength}",
            counterparty.Id,
            counterparty.AccountId,
            field,
            length,
            maxLength);
        return false;
    }

    private void AddOutbox<T>(T @event, Guid accountId, DateTimeOffset now)
    {
        dbContext.OutboxMessages.Add(new SyncOutboxMessage
        {
            Id = Guid.NewGuid(),
            Topic = SyncTopics.Events,
            MessageKey = accountId.ToString("D"),
            EventType = typeof(T).Name,
            Payload = JsonSerializer.Serialize(@event, JsonOptions),
            CreatedAt = now
        });
    }

    private static (string Code, string Message) MapError(Exception exception) =>
        exception switch
        {
            EgressClientException egress => (egress.Code, egress.SafeMessage),
            CounterpartySnapshotChangedException => (
                "COUNTERPARTY_SNAPSHOT_CHANGED",
                "MoySklad counterparty collection changed during synchronization."),
            JsonException => ("JSON_DESERIALIZATION_FAILED", "MoySklad returned invalid counterparty data."),
            DbUpdateException => ("COUNTERPARTY_SAVE_FAILED", "Could not save counterparties."),
            _ => ("SYNC_FAILED", "Synchronization failed.")
        };

    private static void Validate(SyncRequested command)
    {
        if (command.MessageId == Guid.Empty ||
            command.SyncRunId == Guid.Empty ||
            command.AccountId == Guid.Empty ||
            command.RequestedByUserId == Guid.Empty ||
            command.RequestedAt == default ||
            !Enum.IsDefined(command.Mode))
        {
            throw new InvalidOperationException("SyncRequested contains invalid required fields.");
        }
    }

    private static CounterpartySnapshotChangedException SnapshotChanged() =>
        new("MoySklad counterparty collection changed during synchronization.");

    private static DateTimeOffset TruncateToMilliseconds(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var ticks = utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static string ModeName(SyncMode mode) => mode.ToString().ToLowerInvariant();

    private sealed record LoadedSnapshot(
        IReadOnlyList<ParsedCounterparty> Rows,
        int ActiveCount,
        int ArchivedCount,
        int TotalCount);
}
