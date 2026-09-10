using MsContractor.CatalogSyncService.Repositories;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Clients;
using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Sync;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncProcessor
{
    Task ProcessAsync(SyncRequested command, CancellationToken cancellationToken);
}

public sealed class SyncProcessor : ISyncProcessor
{
    private readonly ISyncRepository _repository;
    private readonly IMoySkladEgressClient _egressClient;
    private readonly IMoySkladCounterpartyParser _parser;
    private readonly ICounterpartyNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncProcessor> _logger;

    public SyncProcessor(
        ISyncRepository repository,
        IMoySkladEgressClient egressClient,
        IMoySkladCounterpartyParser parser,
        ICounterpartyNormalizer normalizer,
        TimeProvider timeProvider,
        ILogger<SyncProcessor> logger)
    {
        _repository = repository;
        _egressClient = egressClient;
        _parser = parser;
        _normalizer = normalizer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public const string ConsumerName = "catalog-sync-counterparties-v1";
    private const int PageSize = 1000;
    private static readonly TimeSpan SafetyOverlap = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProcessAsync(
        SyncRequested command,
        CancellationToken cancellationToken)
    {
        Validate(command);

        if (await _repository.HasProcessedAsync(command.AccountId, command.MessageId, ConsumerName, cancellationToken))
        {
            _logger.LogInformation(
                "Sync message already processed: message_id={MessageId}, account_id={AccountId}, sync_run_id={SyncRunId}",
                command.MessageId,
                command.AccountId,
                command.SyncRunId);
            return;
        }

        var run = await EnsureRunningAsync(command, cancellationToken);
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
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
        // Remove dependent document snapshots first, otherwise their FK blocks
        // deletion of the counterparties they reference.
        var (deletedDocumentsCount, deletedCount) = await _repository.ClearSnapshotAsync(command.AccountId, cancellationToken);
        _logger.LogInformation(
            "Existing full-sync snapshot cleared before loading MoySklad: deleted_document_count={DeletedDocumentsCount}, deleted_counterparty_count={DeletedCount}",
            deletedDocumentsCount,
            deletedCount);

        var snapshot = await LoadSnapshotAsync(command, null, null, cancellationToken);
        if (snapshot.Rows.Select(item => item.Value.Id).Distinct().Count() != snapshot.TotalCount)
            throw SnapshotChanged();

        var now = _timeProvider.GetUtcNow();
        var counterparties = snapshot.Rows
            .Select(item => _normalizer.Create(command.AccountId, command.SyncRunId, item, now))
            .Where(IsValidForStorage)
            .ToArray();

        var completion = PrepareCompletion(command, run, snapshot.TotalCount, counterparties.Length, now);
        await _repository.CompleteFullAsync(command.AccountId, counterparties, completion, cancellationToken);

        _logger.LogInformation(
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
        var now = _timeProvider.GetUtcNow();
        var incoming = uniqueRows
            .Select(item => _normalizer.Create(command.AccountId, command.SyncRunId, item, now))
            .Where(IsValidForStorage)
            .ToArray();

        var completion = PrepareCompletion(command, run, snapshot.TotalCount, incoming.Length, now);
        var (insertedCount, updatedCount) = await _repository.CompleteIncrementalAsync(
            command.AccountId, incoming, completion, ApplyIncomingIfCurrent, cancellationToken);

        _logger.LogInformation(
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
        var rawResponse = await _egressClient.GetCounterpartiesAsync(
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
        return _parser.Parse(rawResponse.Json);
    }

    private async Task<SyncRun> EnsureRunningAsync(
        SyncRequested command,
        CancellationToken cancellationToken)
    {
        var run = await _repository.FindRunAsync(command.AccountId, command.MessageId, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        if (run is null)
        {
            var requestedMode = ModeName(command.Mode);
            var watermark = command.Mode == SyncMode.Incremental
                ? await _repository.FindWatermarkAsync(command.AccountId, cancellationToken)
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

        await _repository.SaveRunAsync(command.AccountId, run, cancellationToken);
        return run;
    }

    private static SyncCompletion PrepareCompletion(
        SyncRequested command, SyncRun run, int totalCount, int processedCount, DateTimeOffset now)
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
        return new SyncCompletion(run,
            new InboxMessage { MessageId = command.MessageId, ConsumerName = ConsumerName, ProcessedAt = now },
            CreateOutbox(new SyncCompleted(Guid.NewGuid(), command.SyncRunId, command.AccountId, processedCount, now), command.AccountId, now),
            new SyncWatermark { AccountId = command.AccountId, Watermark = run.WindowTo.Value, LastSyncRunId = command.SyncRunId, UpdatedAt = now });
    }

    private async Task MarkFailedAsync(
        SyncRequested command,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (code, safeMessage) = MapError(exception);
        _logger.LogError(
            exception,
            "Counterparty sync failed: account_id={AccountId}, sync_run_id={SyncRunId}, message_id={MessageId}, requested_by_user_id={UserId}, error_code={ErrorCode}",
            command.AccountId,
            command.SyncRunId,
            command.MessageId,
            command.RequestedByUserId,
            code);

        var run = await _repository.ReloadRunAsync(command.AccountId, command.MessageId, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        run.Status = "failed";
        run.ErrorCode = code;
        run.ErrorMessage = safeMessage;
        run.CompletedAt = now;
        run.UpdatedAt = now;
        await _repository.FailAsync(command.AccountId, run,
            new InboxMessage { MessageId = command.MessageId, ConsumerName = ConsumerName, ProcessedAt = now },
            CreateOutbox(new SyncFailed(Guid.NewGuid(), command.SyncRunId, command.AccountId, code, safeMessage, now), command.AccountId, now),
            cancellationToken);
    }

    private static bool ApplyIncomingIfCurrent(Counterparty target, Counterparty source)
    {
        if (target.MoySkladUpdatedAt is not null && source.MoySkladUpdatedAt is not null &&
            source.MoySkladUpdatedAt < target.MoySkladUpdatedAt)
            return false;
        ApplyIncoming(target, source);
        return true;
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

        _logger.LogWarning(
            "Counterparty skipped because a field exceeds its database limit: counterparty_id={CounterpartyId}, account_id={AccountId}, field={Field}, length={Length}, max_length={MaxLength}",
            counterparty.Id,
            counterparty.AccountId,
            field,
            length,
            maxLength);
        return false;
    }

    private static SyncOutboxMessage CreateOutbox<T>(T @event, Guid accountId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Topic = SyncTopics.Events,
        MessageKey = accountId.ToString("D"),
        EventType = typeof(T).Name,
        Payload = JsonSerializer.Serialize(@event, JsonOptions),
        CreatedAt = now
    };

    private static (string Code, string Message) MapError(Exception exception) =>
        exception switch
        {
            EgressClientException egress => (egress.Code, egress.SafeMessage),
            CounterpartySnapshotChangedException => (
                "COUNTERPARTY_SNAPSHOT_CHANGED",
                "MoySklad counterparty collection changed during synchronization."),
            JsonException => ("JSON_DESERIALIZATION_FAILED", "MoySklad returned invalid counterparty data."),
            CatalogPersistenceException => ("COUNTERPARTY_SAVE_FAILED", "Could not save counterparties."),
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
