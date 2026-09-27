using MsContractor.CatalogSyncService.Repositories;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Clients;
using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Sync;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Services;

public interface ISyncProcessor
{
    Task ProcessAsync(SyncRequested command, CancellationToken cancellationToken);
}

public sealed class SyncProcessor : ISyncProcessor
{
    private readonly ISyncRepository _repository;
    private readonly IMoySkladEgressClient _egressClient;
    private readonly IMoySkladDocumentDiscoveryClient _documentDiscoveryClient;
    private readonly IMoySkladCounterpartyParser _parser;
    private readonly ICounterpartyNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncProcessor> _logger;

    public SyncProcessor(
        ISyncRepository repository,
        IMoySkladEgressClient egressClient,
        IMoySkladDocumentDiscoveryClient documentDiscoveryClient,
        IMoySkladCounterpartyParser parser,
        ICounterpartyNormalizer normalizer,
        TimeProvider timeProvider,
        ILogger<SyncProcessor> logger)
    {
        _repository = repository;
        _egressClient = egressClient;
        _documentDiscoveryClient = documentDiscoveryClient;
        _parser = parser;
        _normalizer = normalizer;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public const string ConsumerName = "catalog-sync-counterparties-v1";
    private const int PageSize = 1000;
    private const int DocumentDiscoveryBatchSize = 50;
    private static readonly TimeSpan RunLeaseDuration = TimeSpan.FromMinutes(2);
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

        var ownerToken = Guid.NewGuid();
        var run = await EnsureRunningAsync(command, ownerToken, cancellationToken);
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
                await ProcessIncrementalAsync(command, run, ownerToken, cancellationToken);
            else
                await ProcessFullAsync(command, run, ownerToken, cancellationToken);
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(command, ownerToken, exception, cancellationToken);
        }
    }

    private async Task ProcessFullAsync(
        SyncRequested command,
        SyncRun run,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadSnapshotAsync(command, run, ownerToken, null, null, cancellationToken);
        await _repository.ValidateFullStagingAsync(
            command.AccountId, command.SyncRunId, snapshot.TotalCount, cancellationToken);
        var archivedIds = await _repository.GetArchivedStagedCounterpartyIdsAsync(
            command.AccountId, command.SyncRunId, incremental: false, cancellationToken: cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var (documents, commissions) = await LoadArchivedDocumentsAsync(command, ownerToken, archivedIds, now, cancellationToken);
        var processedCount = await _repository.GetStagedProcessedCountAsync(
            command.AccountId, command.SyncRunId, incremental: false, cancellationToken: cancellationToken);

        var completion = PrepareCompletion(command, run, snapshot.TotalCount, processedCount, now);
        await _repository.CompleteFullFromStagingAsync(
            command.AccountId, command.SyncRunId, ownerToken, documents, commissions, completion,
            LeaseExpiresAt(now).UtcTicks, cancellationToken);

        _logger.LogInformation(
            "Full counterparty sync completed: processed_count={ProcessedCount}, total_count={TotalCount}, active_count={ActiveCount}, archived_count={ArchivedCount}, archived_document_count={ArchivedDocumentCount}, window_to={WindowTo}",
            processedCount,
            snapshot.TotalCount,
            snapshot.ActiveCount,
            snapshot.ArchivedCount,
            documents.Length,
            run.WindowTo);
    }

    private async Task ProcessIncrementalAsync(
        SyncRequested command,
        SyncRun run,
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        if (run.WindowFrom is null || run.WindowTo is null || run.WindowFrom >= run.WindowTo)
            throw new InvalidOperationException("Incremental sync window is invalid.");

        var snapshot = await LoadSnapshotAsync(
            command,
            run,
            ownerToken,
            run.WindowFrom,
            run.WindowTo,
            cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var archivedIds = await _repository.GetArchivedStagedCounterpartyIdsAsync(
            command.AccountId, command.SyncRunId, incremental: true, cancellationToken: cancellationToken);
        var (documents, commissions) = await LoadArchivedDocumentsAsync(command, ownerToken, archivedIds, now, cancellationToken);
        var processedCount = await _repository.GetStagedProcessedCountAsync(
            command.AccountId, command.SyncRunId, incremental: true, cancellationToken: cancellationToken);

        var completion = PrepareCompletion(command, run, snapshot.TotalCount, processedCount, now);
        var (insertedCount, updatedCount) = await _repository.CompleteIncrementalFromStagingAsync(
            command.AccountId, command.SyncRunId, ownerToken, documents, commissions, completion,
            ApplyIncomingIfCurrent, LeaseExpiresAt(now).UtcTicks, cancellationToken);

        _logger.LogInformation(
            "Incremental counterparty sync completed: processed_count={ProcessedCount}, total_count={TotalCount}, inserted_count={InsertedCount}, updated_count={UpdatedCount}, active_count={ActiveCount}, archived_count={ArchivedCount}, archived_document_count={ArchivedDocumentCount}, window_from={WindowFrom}, window_to={WindowTo}",
            processedCount,
            snapshot.TotalCount,
            insertedCount,
            updatedCount,
            snapshot.ActiveCount,
            snapshot.ArchivedCount,
            documents.Length,
            run.WindowFrom,
            run.WindowTo);
    }

    private async Task<(CounterpartyDocument[] Documents, DocumentAdditionalCommission[] Commissions)> LoadArchivedDocumentsAsync(
        SyncRequested command,
        Guid ownerToken,
        IReadOnlyCollection<Guid> archivedIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (archivedIds.Count == 0)
            return ([], []);

        var knownCounterpartyIds = archivedIds.ToHashSet();
        var seenDocumentIds = new HashSet<Guid>();
        var documents = new List<CounterpartyDocument>();
        var commissions = new List<DocumentAdditionalCommission>();
        foreach (var batch in archivedIds.Chunk(DocumentDiscoveryBatchSize))
        {
            var batchIds = batch.ToArray();
            var batchCounterpartyIds = batchIds.ToHashSet();
            if (!await _repository.RenewRunLeaseAsync(
                    command.AccountId, command.SyncRunId, ownerToken, LeaseExpiresAt(_timeProvider.GetUtcNow()).UtcTicks, cancellationToken))
                throw new SyncRunLeaseLostException("The sync run lease was lost before archived document discovery.");
            var response = await _documentDiscoveryClient.DiscoverAsync(
                command.AccountId,
                batchIds,
                command.RequestedByUserId,
                command.MessageId.ToString("D"),
                cancellationToken);
            if (response?.Documents is null)
                throw InvalidDiscoveryResponse();

            foreach (var document in response.Documents)
            {
                if (document is null || string.IsNullOrWhiteSpace(document.DocumentType) || document.DocumentType.Length > 64 ||
                    document.DocumentId == Guid.Empty ||
                    !batchCounterpartyIds.Contains(document.CounterpartyId) ||
                    !knownCounterpartyIds.Contains(document.CounterpartyId) ||
                    !seenDocumentIds.Add(document.DocumentId))
                {
                    throw InvalidDiscoveryResponse();
                }

                documents.Add(new CounterpartyDocument
                {
                    AccountId = command.AccountId,
                    CounterpartyId = document.CounterpartyId,
                    DocumentType = document.DocumentType,
                    DocumentId = document.DocumentId,
                    UpdatedAt = now
                });

                if (IsCommissionReport(document.DocumentType))
                {
                    commissions.Add(new DocumentAdditionalCommission
                    {
                        DocumentId = document.DocumentId,
                        Contract = document.ContractId
                    });
                }
            }
        }

        return (documents.ToArray(), commissions.ToArray());
    }

    private static bool IsCommissionReport(string documentType) =>
        string.Equals(documentType, "commissionreportin", StringComparison.Ordinal) ||
        string.Equals(documentType, "commissionreportout", StringComparison.Ordinal);

    private static EgressClientException InvalidDiscoveryResponse() =>
        new("EGRESS_INVALID_RESPONSE", "MoySklad Egress Service returned an inconsistent document discovery response.", 502);

    private async Task<LoadedSnapshot> LoadSnapshotAsync(
        SyncRequested command,
        SyncRun run,
        Guid ownerToken,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        var activeCount = await GetCountAsync(command, ownerToken, false, windowFrom, windowTo, cancellationToken);
        var archivedCount = await GetCountAsync(command, ownerToken, true, windowFrom, windowTo, cancellationToken);
        var totalCount = checked(activeCount + archivedCount);
        run.TotalCount = totalCount;
        await _repository.SetTotalCountAsync(
            command.AccountId, command.SyncRunId, ownerToken, totalCount,
            LeaseExpiresAt(_timeProvider.GetUtcNow()).UtcTicks, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var activeLoaded = await LoadPagesAsync(
            command, ownerToken, false, activeCount, 0, windowFrom, windowTo, run.ExecutionMode == ModeName(SyncMode.Incremental), now, cancellationToken);
        var archivedLoaded = await LoadPagesAsync(
            command, ownerToken, true, archivedCount, activeCount, windowFrom, windowTo, run.ExecutionMode == ModeName(SyncMode.Incremental), now, cancellationToken);
        if (activeLoaded + archivedLoaded != totalCount)
            throw SnapshotChanged();
        return new LoadedSnapshot(activeCount, archivedCount, totalCount);
    }

    private async Task<int> GetCountAsync(
        SyncRequested command,
        Guid ownerToken,
        bool archived,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        var parsed = await GetPageAsync(command, ownerToken, archived, 1, 0, windowFrom, windowTo, cancellationToken);
        if (parsed.Meta.Size < 0)
            throw SnapshotChanged();
        return parsed.Meta.Size;
    }

    private async Task<int> LoadPagesAsync(
        SyncRequested command,
        Guid ownerToken,
        bool archived,
        int expectedCount,
        long sequenceBase,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        bool incremental,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var loadedCount = 0;
        for (var offset = 0; offset < expectedCount; offset += PageSize)
        {
            var parsed = await GetPageAsync(
                command,
                ownerToken,
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

            var stageRows = new CounterpartySyncStage[parsed.Rows.Count];
            for (var index = 0; index < parsed.Rows.Count; index++)
            {
                var normalized = _normalizer.Create(command.AccountId, command.SyncRunId, parsed.Rows[index], now);
                stageRows[index] = ToStage(
                    normalized,
                    sequenceBase + offset + index,
                    IsValidForStorage(normalized));
            }
            await _repository.StagePageAsync(
                command.AccountId, command.SyncRunId, ownerToken, stageRows, incremental, now,
                LeaseExpiresAt(_timeProvider.GetUtcNow()).UtcTicks, cancellationToken);
            loadedCount += parsed.Rows.Count;
        }

        return loadedCount;
    }

    private static CounterpartySyncStage ToStage(Counterparty counterparty, long sequence, bool isValidForStorage) => new()
    {
        AccountId = counterparty.AccountId,
        SyncRunId = counterparty.LastSyncRunId,
        Sequence = sequence,
        CounterpartyId = counterparty.Id,
        IsValidForStorage = isValidForStorage,
        Name = counterparty.Name,
        Phone = counterparty.Phone,
        Email = counterparty.Email,
        Inn = counterparty.Inn,
        Kpp = counterparty.Kpp,
        Description = counterparty.Description,
        Archived = counterparty.Archived,
        NormalizedName = counterparty.NormalizedName,
        NormalizedPhone = counterparty.NormalizedPhone,
        NormalizedEmail = counterparty.NormalizedEmail,
        NormalizedInn = counterparty.NormalizedInn,
        NormalizedKpp = counterparty.NormalizedKpp,
        MoySkladUpdatedAt = counterparty.MoySkladUpdatedAt,
        MoySkladUpdatedSortValue = counterparty.MoySkladUpdatedAt?.UtcTicks ?? long.MinValue,
        LastSyncRunId = counterparty.LastSyncRunId,
        RawJson = counterparty.RawJson,
        CreatedAt = counterparty.CreatedAt,
        UpdatedAt = counterparty.UpdatedAt
    };

    private async Task<ParsedCounterpartyCollection> GetPageAsync(
        SyncRequested command,
        Guid ownerToken,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        CancellationToken cancellationToken)
    {
        if (!await _repository.RenewRunLeaseAsync(
                command.AccountId, command.SyncRunId, ownerToken,
                LeaseExpiresAt(_timeProvider.GetUtcNow()).UtcTicks, cancellationToken))
            throw new SyncRunLeaseLostException("The sync run lease was lost before fetching a counterparty page.");

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
        Guid ownerToken,
        CancellationToken cancellationToken)
    {
        var run = await _repository.FindRunAsync(command.AccountId, command.MessageId, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var isNew = run is null;
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

        }

        if (isNew)
            await _repository.SaveRunAsync(command.AccountId, run, cancellationToken);

        var leaseNow = _timeProvider.GetUtcNow();
        var leaseExpiresAt = LeaseExpiresAt(leaseNow);
        if (!await _repository.TryAcquireRunLeaseAsync(
                command.AccountId, command.SyncRunId, ownerToken,
                leaseNow.UtcTicks, leaseExpiresAt.UtcTicks, cancellationToken))
            throw new SyncRunAlreadyOwnedException("Another worker currently owns this counterparty sync run.");

        run.Status = "running";
        run.StartedAt ??= leaseNow;
        run.WindowTo ??= TruncateToMilliseconds(leaseNow);
        run.UpdatedAt = leaseNow;
        run.CompletedAt = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        run.ProcessingOwnerToken = ownerToken;
        run.ProcessingLeaseExpiresAtTicks = leaseExpiresAt.UtcTicks;
        await _repository.RestartStagingAsync(
            command.AccountId, command.SyncRunId, ownerToken, leaseNow, run.WindowTo.Value,
            leaseExpiresAt.UtcTicks, cancellationToken);
        run.TotalCount = 0;
        run.ProcessedCount = 0;
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
        run.ProcessingOwnerToken = null;
        run.ProcessingLeaseExpiresAtTicks = null;
        return new SyncCompletion(run,
            new InboxMessage { MessageId = command.MessageId, ConsumerName = ConsumerName, ProcessedAt = now },
            CreateOutbox(new SyncCompleted(Guid.NewGuid(), command.SyncRunId, command.AccountId, processedCount, now), command.AccountId, now),
            new SyncWatermark { AccountId = command.AccountId, Watermark = run.WindowTo.Value, LastSyncRunId = command.SyncRunId, UpdatedAt = now });
    }

    private async Task MarkFailedAsync(
        SyncRequested command,
        Guid ownerToken,
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

        var now = _timeProvider.GetUtcNow();
        var failed = await _repository.FailAsync(
            command.AccountId, command.SyncRunId, ownerToken, code, safeMessage, now,
            new InboxMessage { MessageId = command.MessageId, ConsumerName = ConsumerName, ProcessedAt = now },
            CreateOutbox(new SyncFailed(Guid.NewGuid(), command.SyncRunId, command.AccountId, code, safeMessage, now), command.AccountId, now),
            cancellationToken);
        if (!failed)
        {
            _logger.LogInformation(
                "Skipping sync failure transition because this worker no longer owns the run: account_id={AccountId}, sync_run_id={SyncRunId}",
                command.AccountId,
                command.SyncRunId);
            throw new SyncRunLeaseLostException(
                "The sync run lease was lost before this worker could persist the failure; retry the command.");
        }
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

    private DateTimeOffset LeaseExpiresAt(DateTimeOffset now) => now.Add(RunLeaseDuration);

    private static string ModeName(SyncMode mode) => mode.ToString().ToLowerInvariant();

    private sealed record LoadedSnapshot(int ActiveCount, int ArchivedCount, int TotalCount);
}
