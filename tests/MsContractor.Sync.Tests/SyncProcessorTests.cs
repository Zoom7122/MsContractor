using MsContractor.CatalogSyncService.Repositories;
using MsContractor.CatalogSyncService.Clients;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Persistence;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Sync;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class SyncProcessorTests
{
    [Fact]
    public async Task FullSync_ReplacesOnlyCurrentAccountAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);

        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var existingCurrent = Existing(accountId, "Старый текущего аккаунта");
        var existingOther = Existing(otherAccountId, "Контрагент другого аккаунта");
        dbContext.Counterparties.AddRange(existingCurrent, existingOther);
        await dbContext.SaveChangesAsync();
        dbContext.CounterpartyDocuments.AddRange(
            new CounterpartyDocument
            {
                AccountId = accountId,
                CounterpartyId = existingCurrent.Id,
                DocumentType = "purchasereturn",
                DocumentId = Guid.NewGuid(),
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new CounterpartyDocument
            {
                AccountId = otherAccountId,
                CounterpartyId = existingOther.Id,
                DocumentType = "purchasereturn",
                DocumentId = Guid.NewGuid(),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();

        var moySkladId = Guid.NewGuid();
        var catalogHadExistingRowsAtFirstRequest = false;
        var egress = new FakeEgressClient(request =>
        {
            if (request == new PageRequest(false, 1, 0))
                catalogHadExistingRowsAtFirstRequest = dbContext.Counterparties.Any(item => item.AccountId == accountId);
            return request switch
            {
                { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
                { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
                { Archived: false, Limit: 1000, Offset: 0 } =>
                    Collection(1, 1000, 0, [new Row(moySkladId, "Новый", false)]),
                _ => throw new InvalidOperationException($"Unexpected request: {request}")
            };
        });
        var processor = CreateProcessor(dbContext, egress);
        var command = Command(accountId);

        await processor.ProcessAsync(command, CancellationToken.None);
        await processor.ProcessAsync(command, CancellationToken.None);

        var current = await dbContext.Counterparties.Where(item => item.AccountId == accountId).ToListAsync();
        var other = await dbContext.Counterparties.Where(item => item.AccountId == otherAccountId).ToListAsync();
        Assert.Single(current);
        Assert.Equal(moySkladId, current[0].Id);
        Assert.Single(other);
        Assert.Equal("Контрагент другого аккаунта", other[0].Name);
        var remainingDocuments = await dbContext.CounterpartyDocuments.ToListAsync();
        Assert.Single(remainingDocuments);
        Assert.Equal(otherAccountId, remainingDocuments[0].AccountId);
        Assert.Equal(existingOther.Id, remainingDocuments[0].CounterpartyId);
        Assert.True(catalogHadExistingRowsAtFirstRequest);
        Assert.Equal(3, egress.Requests.Count);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("completed", run.Status);
        Assert.Equal(1, run.TotalCount);
        Assert.Equal(1, run.ProcessedCount);
        Assert.Equal(run.WindowTo, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
        Assert.Single(dbContext.InboxMessages);
        Assert.Single(dbContext.OutboxMessages);
    }

    [Fact]
    public async Task FullSync_LoadsAllActivePagesBeforeArchivedPages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var active = Enumerable.Range(0, 2001)
            .Select(index => new Row(Guid.NewGuid(), $"Активный {index}", false))
            .ToArray();
        var archived = Enumerable.Range(0, 2)
            .Select(index => new Row(Guid.NewGuid(), $"Архивный {index}", true))
            .ToArray();
        var egress = new FakeEgressClient(request =>
        {
            var source = request.Archived ? archived : active;
            return request.Limit == 1
                ? Collection(source.Length, 1, 0, [])
                : Collection(
                    source.Length,
                    request.Limit,
                    request.Offset,
                    source.Skip(request.Offset).Take(request.Limit));
        });

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(Command(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(
            [
                new PageRequest(false, 1, 0),
                new PageRequest(true, 1, 0),
                new PageRequest(false, 1000, 0),
                new PageRequest(false, 1000, 1000),
                new PageRequest(false, 1000, 2000),
                new PageRequest(true, 1000, 0)
            ],
            egress.Requests);
        Assert.Equal(2003, await dbContext.Counterparties.CountAsync());
        Assert.Equal(2, await dbContext.Counterparties.CountAsync(item => item.Archived));
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("completed", run.Status);
        Assert.Equal(2003, run.TotalCount);
        Assert.Equal(2003, run.ProcessedCount);
    }

    [Fact]
    public async Task FullSync_PersistsEachPageAndProgressBeforeFetchingTheNextPage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var existing = Existing(accountId, "Visible until publication");
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();

        var command = Command(accountId);
        var rows = Enumerable.Range(0, 1001)
            .Select(index => new Row(Guid.NewGuid(), $"КА {index}", false))
            .ToArray();
        var observedFirstPageBeforeNextRequest = false;
        var egress = new FakeEgressClient(request =>
        {
            if (request == new PageRequest(false, 1000, 1000))
            {
                var run = dbContext.SyncRuns.AsNoTracking().Single(item => item.Id == command.SyncRunId);
                observedFirstPageBeforeNextRequest =
                    dbContext.CounterpartySyncStaging.AsNoTracking().Count(item =>
                        item.AccountId == accountId && item.SyncRunId == command.SyncRunId) == 1000 &&
                    run.TotalCount == 1001 && run.ProcessedCount == 1000 &&
                    dbContext.Counterparties.AsNoTracking().Any(item => item.Id == existing.Id);
            }

            var source = request.Archived ? Array.Empty<Row>() : rows;
            return request.Limit == 1
                ? Collection(source.Length, 1, 0, [])
                : Collection(source.Length, request.Limit, request.Offset,
                    source.Skip(request.Offset).Take(request.Limit));
        });

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(command, CancellationToken.None);

        Assert.True(observedFirstPageBeforeNextRequest);
        Assert.Equal(1001, await dbContext.Counterparties.CountAsync(item => item.AccountId == accountId));
        Assert.False(await dbContext.Counterparties.AnyAsync(item => item.Id == existing.Id));
        Assert.Empty(await dbContext.CounterpartySyncStaging.ToListAsync());
        var completed = await dbContext.SyncRuns.SingleAsync(item => item.Id == command.SyncRunId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(1001, completed.ProcessedCount);
    }

    [Fact]
    public async Task StillRunningRedelivery_ClearsOldStagingAndProgressBeforeRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var command = Command(accountId);
        var now = DateTimeOffset.UtcNow;
        dbContext.SyncRuns.Add(new SyncRun
        {
            Id = command.SyncRunId,
            MessageId = command.MessageId,
            AccountId = accountId,
            RequestedByUserId = command.RequestedByUserId,
            RequestedMode = "full",
            ExecutionMode = "full",
            Status = "running",
            ProcessedCount = 1,
            TotalCount = 1,
            WindowTo = now,
            StartedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        dbContext.CounterpartySyncStaging.Add(new CounterpartySyncStage
        {
            AccountId = accountId,
            SyncRunId = command.SyncRunId,
            Sequence = 10,
            CounterpartyId = Guid.NewGuid(),
            IsValidForStorage = true,
            Name = "Stale staged row",
            NormalizedName = "stale staged row",
            LastSyncRunId = command.SyncRunId,
            RawJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync();
        var resetWasVisibleBeforeEgress = false;
        var egress = new FakeEgressClient(request =>
        {
            if (request == new PageRequest(false, 1, 0))
            {
                var run = dbContext.SyncRuns.AsNoTracking().Single(item => item.Id == command.SyncRunId);
                resetWasVisibleBeforeEgress =
                    !dbContext.CounterpartySyncStaging.AsNoTracking().Any(item => item.SyncRunId == command.SyncRunId) &&
                    run.ProcessedCount == 0 && run.TotalCount == 0;
            }
            return Collection(0, request.Limit, request.Offset, []);
        });

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(command, CancellationToken.None);

        Assert.True(resetWasVisibleBeforeEgress);
        Assert.Empty(await dbContext.CounterpartySyncStaging.ToListAsync());
        Assert.Equal("completed", (await dbContext.SyncRuns.SingleAsync(item => item.Id == command.SyncRunId)).Status);
    }

    [Fact]
    public async Task ConcurrentRedelivery_CannotResetOwnedStagingOrFailCompletedRun()
    {
        var databaseName = $"catalog-sync-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using var ownerContext = await CreateContextAsync(connectionString);
        await using var contenderContext = await CreateContextAsync(connectionString);

        var accountId = Guid.NewGuid();
        var command = Command(accountId);
        var rows = Enumerable.Range(0, 1001)
            .Select(index => new Row(Guid.NewGuid(), $"КА {index}", false))
            .ToArray();
        var secondPageRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondPage = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerEgress = new BlockingEgressClient(async (request, cancellationToken) =>
        {
            if (request.Limit == 1)
                return Collection(request.Archived ? 0 : rows.Length, 1, 0, []);

            if (request.Archived)
                return Collection(0, request.Limit, request.Offset, []);

            if (request.Offset == 1000)
            {
                secondPageRequested.TrySetResult(true);
                await releaseSecondPage.Task.WaitAsync(cancellationToken);
            }

            return Collection(rows.Length, request.Limit, request.Offset,
                rows.Skip(request.Offset).Take(request.Limit));
        });

        var ownerTask = CreateProcessor(ownerContext, ownerEgress)
            .ProcessAsync(command, CancellationToken.None);
        try
        {
            await secondPageRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var runWhileOwned = await contenderContext.SyncRuns.AsNoTracking()
                .SingleAsync(item => item.Id == command.SyncRunId);
            Assert.Equal("running", runWhileOwned.Status);
            Assert.NotNull(runWhileOwned.ProcessingOwnerToken);
            Assert.Equal(1001, runWhileOwned.TotalCount);
            Assert.Equal(1000, runWhileOwned.ProcessedCount);
            Assert.Equal(1000, await contenderContext.CounterpartySyncStaging.AsNoTracking()
                .CountAsync(item => item.SyncRunId == command.SyncRunId));

            var contenderEgress = new FakeEgressClient(_ => throw new InvalidOperationException("The contender must not call Egress."));
            await Assert.ThrowsAsync<SyncRunAlreadyOwnedException>(() =>
                CreateProcessor(contenderContext, contenderEgress).ProcessAsync(command, CancellationToken.None));

            Assert.Empty(contenderEgress.Requests);
            Assert.Equal(1000, await contenderContext.CounterpartySyncStaging.AsNoTracking()
                .CountAsync(item => item.SyncRunId == command.SyncRunId));
            runWhileOwned = await contenderContext.SyncRuns.AsNoTracking()
                .SingleAsync(item => item.Id == command.SyncRunId);
            Assert.Equal("running", runWhileOwned.Status);
            Assert.Equal(1000, runWhileOwned.ProcessedCount);
        }
        finally
        {
            releaseSecondPage.TrySetResult(true);
        }

        await ownerTask;
        var completed = await contenderContext.SyncRuns.AsNoTracking()
            .SingleAsync(item => item.Id == command.SyncRunId);
        Assert.Equal("completed", completed.Status);
        Assert.Null(completed.ProcessingOwnerToken);
        Assert.Empty(await contenderContext.CounterpartySyncStaging.ToListAsync());

        var failed = await new SyncRepository(contenderContext).FailAsync(
            accountId,
            command.SyncRunId,
            Guid.NewGuid(),
            "SYNC_FAILED",
            "Stale owner failure",
            DateTimeOffset.UtcNow,
            new InboxMessage { MessageId = command.MessageId, ConsumerName = SyncProcessor.ConsumerName, ProcessedAt = DateTimeOffset.UtcNow },
            new SyncOutboxMessage
            {
                Id = Guid.NewGuid(),
                Topic = "catalog-sync.events",
                MessageKey = accountId.ToString("D"),
                EventType = "SyncFailed",
                Payload = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            },
            CancellationToken.None);

        Assert.False(failed);
        Assert.Equal("completed", (await contenderContext.SyncRuns.AsNoTracking()
            .SingleAsync(item => item.Id == command.SyncRunId)).Status);
        Assert.Equal("SyncCompleted", (await contenderContext.OutboxMessages.AsNoTracking().SingleAsync()).EventType);
    }

    [Fact]
    public async Task FailedRunWithInbox_CannotBeReacquiredByDeliveryThatPassedItsInboxCheckEarlier()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var command = Command(accountId);
        var now = DateTimeOffset.UtcNow;
        dbContext.SyncRuns.Add(new SyncRun
        {
            Id = command.SyncRunId,
            MessageId = command.MessageId,
            AccountId = accountId,
            RequestedByUserId = command.RequestedByUserId,
            RequestedMode = "full",
            ExecutionMode = "full",
            Status = "failed",
            ProcessedCount = 0,
            TotalCount = 1,
            StartedAt = now.AddMinutes(-1),
            CompletedAt = now,
            WindowTo = now,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now
        });
        dbContext.InboxMessages.Add(new InboxMessage
        {
            MessageId = command.MessageId,
            ConsumerName = SyncProcessor.ConsumerName,
            ProcessedAt = now
        });
        await dbContext.SaveChangesAsync();

        var acquired = await new SyncRepository(dbContext).TryAcquireRunLeaseAsync(
            accountId,
            command.SyncRunId,
            Guid.NewGuid(),
            now.UtcTicks,
            now.AddMinutes(2).UtcTicks,
            CancellationToken.None);

        Assert.False(acquired);
        var failedRun = await dbContext.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == command.SyncRunId);
        Assert.Equal("failed", failedRun.Status);
        Assert.Null(failedRun.ProcessingOwnerToken);
        Assert.True(await dbContext.InboxMessages.AnyAsync(item => item.MessageId == command.MessageId));
    }

    [Fact]
    public async Task StaleOwnerFailure_PropagatesLeaseLossSoConsumerCanRetryOffset()
    {
        var databaseName = $"catalog-sync-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using var ownerContext = await CreateContextAsync(connectionString);
        await using var takeoverContext = await CreateContextAsync(connectionString);

        var accountId = Guid.NewGuid();
        var command = Command(accountId);
        var replacementOwnerToken = Guid.NewGuid();
        var egress = new FakeEgressClient(request =>
        {
            if (request.Limit == 1)
                return Collection(request.Archived ? 0 : 1, 1, 0, []);
            if (request.Archived)
                return Collection(0, request.Limit, request.Offset, []);

            var claimedByOtherWorker = takeoverContext.SyncRuns
                .Where(item => item.AccountId == accountId && item.Id == command.SyncRunId && item.Status == "running")
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.ProcessingOwnerToken, replacementOwnerToken)
                    .SetProperty(item => item.ProcessingLeaseExpiresAtTicks, DateTimeOffset.UtcNow.AddMinutes(2).UtcTicks))
                .GetAwaiter()
                .GetResult();
            Assert.Equal(1, claimedByOtherWorker);
            throw new EgressClientException("EGRESS_UNAVAILABLE", "Egress is unavailable.", 503);
        });

        await Assert.ThrowsAsync<SyncRunLeaseLostException>(() =>
            CreateProcessor(ownerContext, egress).ProcessAsync(command, CancellationToken.None));

        var run = await takeoverContext.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == command.SyncRunId);
        Assert.Equal("running", run.Status);
        Assert.Equal(replacementOwnerToken, run.ProcessingOwnerToken);
        Assert.Empty(await takeoverContext.InboxMessages.ToListAsync());
        Assert.Empty(await takeoverContext.OutboxMessages.ToListAsync());
        Assert.Empty(await takeoverContext.CounterpartySyncStaging.ToListAsync());
    }

    [Fact]
    public async Task FullSync_RequestsExactlyOnePageForExactlyOneThousandItems()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var rows = Enumerable.Range(0, 1000)
            .Select(index => new Row(Guid.NewGuid(), $"КА {index}", false))
            .ToArray();
        var egress = new FakeEgressClient(request => request.Limit == 1
            ? Collection(request.Archived ? 0 : rows.Length, 1, 0, [])
            : Collection(rows.Length, request.Limit, request.Offset, rows));

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(Command(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(3, egress.Requests.Count);
        Assert.Equal(new PageRequest(false, 1000, 0), egress.Requests[2]);
        Assert.Equal(1000, await dbContext.Counterparties.CountAsync());
    }

    [Fact]
    public async Task FullSync_LoadsArchivedDocumentsInBatchesAndStoresCommissionContracts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var active = new Row(Guid.NewGuid(), "Активный", false);
        var archived = Enumerable.Range(0, 51)
            .Select(index => new Row(Guid.NewGuid(), $"Архивный {index}", true))
            .ToArray();
        var egress = new FakeEgressClient(request =>
        {
            var source = request.Archived ? archived : [active];
            return request.Limit == 1
                ? Collection(source.Length, 1, 0, [])
                : Collection(source.Length, request.Limit, request.Offset, source.Skip(request.Offset).Take(request.Limit));
        });
        var discovery = new FakeDocumentDiscoveryClient(request => new MoySkladDocumentDiscoveryResponse(
            request.CounterpartyIds.Select(counterpartyId => new MoySkladDocumentReference(
                "commissionreportin", Guid.NewGuid(), counterpartyId, Guid.NewGuid())).ToArray(), []));

        await CreateProcessor(dbContext, egress, discovery: discovery)
            .ProcessAsync(Command(Guid.NewGuid()), CancellationToken.None);

        Assert.Equal([50, 1], discovery.Requests.Select(request => request.CounterpartyIds.Length));
        Assert.All(discovery.Requests, request => Assert.DoesNotContain(active.Id, request.CounterpartyIds));
        Assert.Equal(archived.Select(row => row.Id).Order(), discovery.Requests.SelectMany(request => request.CounterpartyIds).Order());
        Assert.Equal(51, await dbContext.CounterpartyDocuments.CountAsync());
        var commission = await dbContext.DocumentAdditionalCommissions.ToListAsync();
        Assert.Equal(51, commission.Count);
        Assert.All(commission, item => Assert.NotNull(item.Contract));
    }

    [Fact]
    public async Task FullSync_DiscoveryFailurePreservesPreviousSnapshotAndWatermark()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var existing = Existing(accountId, "Сохраненный архивный КА");
        existing.Archived = true;
        dbContext.Counterparties.Add(existing);
        var oldDocumentId = Guid.NewGuid();
        dbContext.CounterpartyDocuments.Add(new CounterpartyDocument
        {
            AccountId = accountId,
            CounterpartyId = existing.Id,
            DocumentType = "purchasereturn",
            DocumentId = oldDocumentId,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        var oldWatermark = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = oldWatermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = oldWatermark
        });
        await dbContext.SaveChangesAsync();

        var archived = Enumerable.Range(0, 51)
            .Select(index => new Row(Guid.NewGuid(), $"Новый архивный {index}", true))
            .ToArray();
        var egress = new FakeEgressClient(request =>
        {
            if (request.Limit == 1)
                return Collection(request.Archived ? archived.Length : 0, 1, 0, []);
            return Collection(archived.Length, request.Limit, request.Offset, archived.Skip(request.Offset).Take(request.Limit));
        });
        var discovery = new FakeDocumentDiscoveryClient(request => request.CounterpartyIds.Length == 50
            ? new MoySkladDocumentDiscoveryResponse([], [])
            : throw new EgressClientException("EGRESS_UNAVAILABLE", "Egress is unavailable.", 503));

        await CreateProcessor(dbContext, egress, discovery: discovery)
            .ProcessAsync(Command(accountId), CancellationToken.None);

        Assert.Equal([50, 1], discovery.Requests.Select(request => request.CounterpartyIds.Length));
        dbContext.ChangeTracker.Clear();
        Assert.Equal(existing.Id, (await dbContext.Counterparties.SingleAsync()).Id);
        Assert.Equal(oldDocumentId, (await dbContext.CounterpartyDocuments.SingleAsync()).DocumentId);
        Assert.Equal(oldWatermark, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
        Assert.Empty(await dbContext.CounterpartySyncStaging.ToListAsync());
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("failed", run.Status);
        Assert.Equal("EGRESS_UNAVAILABLE", run.ErrorCode);
    }

    [Fact]
    public async Task IncrementalSync_EmptyArchivedDiscoveryRemovesPreviousDocumentLinks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var watermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = watermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = watermark
        });
        var existing = Existing(accountId, "Архивный КА");
        existing.Archived = true;
        dbContext.Counterparties.Add(existing);
        var oldDocumentId = Guid.NewGuid();
        dbContext.CounterpartyDocuments.Add(new CounterpartyDocument
        {
            AccountId = accountId,
            CounterpartyId = existing.Id,
            DocumentType = "commissionreportin",
            DocumentId = oldDocumentId,
            UpdatedAt = watermark
        });
        dbContext.DocumentAdditionalCommissions.Add(new DocumentAdditionalCommission
        {
            DocumentId = oldDocumentId,
            Contract = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();
        var updatedAt = watermark.AddMinutes(10);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(0, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(1, 1, 0, []),
            _ => Collection(1, request.Limit, request.Offset, [new Row(existing.Id, existing.Name, true, updatedAt)])
        });
        var discovery = new FakeDocumentDiscoveryClient(_ => new MoySkladDocumentDiscoveryResponse([], []));

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(updatedAt.AddMinutes(5)), discovery)
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        Assert.Single(discovery.Requests);
        Assert.Equal([existing.Id], discovery.Requests[0].CounterpartyIds);
        Assert.Empty(await dbContext.CounterpartyDocuments.ToListAsync());
        Assert.Empty(await dbContext.DocumentAdditionalCommissions.ToListAsync());
        Assert.Equal("completed", (await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical")).Status);
    }

    [Fact]
    public async Task FullSync_SkipsCounterpartyWhoseFieldExceedsDatabaseLimit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var validId = Guid.NewGuid();
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(2, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(2, request.Limit, request.Offset,
                [new Row(validId, "Подходит", false), new Row(Guid.NewGuid(), new string('К', 1025), false)])
        });
        var command = Command(accountId);

        await CreateProcessor(dbContext, egress).ProcessAsync(command, CancellationToken.None);

        var saved = await dbContext.Counterparties.SingleAsync();
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Id == command.SyncRunId);
        Assert.Equal(validId, saved.Id);
        Assert.Equal(2, run.TotalCount);
        Assert.Equal(1, run.ProcessedCount);
        Assert.Equal("completed", run.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullSync_PreservesPreviousSnapshotForIncompleteOrDuplicateSnapshot(bool duplicate)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var existing = Existing(accountId, "Старый");
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var id = Guid.NewGuid();
        var page = duplicate
            ? new[] { new Row(id, "Первый", false), new Row(id, "Повтор", false) }
            : new[] { new Row(id, "Только один", false) };
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(2, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(2, request.Limit, request.Offset, page)
        });

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(Command(accountId), CancellationToken.None);

        var preserved = await dbContext.Counterparties.Where(item => item.AccountId == accountId).ToListAsync();
        Assert.Equal(existing.Id, Assert.Single(preserved).Id);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("failed", run.Status);
        Assert.Equal("COUNTERPARTY_SNAPSHOT_CHANGED", run.ErrorCode);
        Assert.Empty(await dbContext.CounterpartySyncStaging.ToListAsync());
    }

    [Fact]
    public async Task FullSync_PreservesPreviousSnapshotWhenMetaSizeChanges()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var existing = Existing(accountId, "Старый");
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(2, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(
                3,
                request.Limit,
                request.Offset,
                [new Row(Guid.NewGuid(), "Первый", false), new Row(Guid.NewGuid(), "Второй", false)])
        });

        await CreateProcessor(dbContext, egress)
            .ProcessAsync(Command(accountId), CancellationToken.None);

        var preserved = await dbContext.Counterparties.Where(item => item.AccountId == accountId).ToListAsync();
        Assert.Equal(existing.Id, Assert.Single(preserved).Id);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("failed", run.Status);
        Assert.Equal("COUNTERPARTY_SNAPSHOT_CHANGED", run.ErrorCode);
    }

    [Fact]
    public async Task IncrementalWithoutWatermark_PerformsFullBootstrapAndCreatesWatermark()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        dbContext.Counterparties.Add(Existing(accountId, "Старый"));
        await dbContext.SaveChangesAsync();
        var newId = Guid.NewGuid();
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(1, request.Limit, request.Offset, [new Row(newId, "Bootstrap", false)])
        });
        var now = new DateTimeOffset(2026, 3, 20, 9, 10, 11, 123, TimeSpan.Zero).AddTicks(4567);

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(now))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        var saved = await dbContext.Counterparties.Where(item => item.AccountId == accountId).SingleAsync();
        Assert.Equal(newId, saved.Id);
        Assert.All(egress.Requests, request =>
        {
            Assert.Null(request.WindowFrom);
            Assert.Null(request.WindowTo);
        });
        var expectedWindowTo = new DateTimeOffset(2026, 3, 20, 9, 10, 11, 123, TimeSpan.Zero);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("incremental", run.RequestedMode);
        Assert.Equal("full", run.ExecutionMode);
        Assert.Null(run.WindowFrom);
        Assert.Equal(expectedWindowTo, run.WindowTo);
        var watermark = await dbContext.SyncWatermarks.SingleAsync();
        Assert.Equal(expectedWindowTo, watermark.Watermark);
        Assert.Equal(run.Id, watermark.LastSyncRunId);
    }

    [Fact]
    public async Task Incremental_UsesSafetyOverlapAndUpsertsActiveThenArchived()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var watermarkValue = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = watermarkValue,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = watermarkValue
        });
        var existing = Existing(accountId, "Старое имя");
        existing.CreatedAt = watermarkValue.AddDays(-1);
        existing.MoySkladUpdatedAt = watermarkValue.AddMinutes(-2);
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var originalId = existing.Id;
        var originalCreatedAt = existing.CreatedAt;
        var newId = Guid.NewGuid();
        var activeId = Guid.NewGuid();
        var windowTo = new DateTimeOffset(2026, 3, 20, 10, 0, 0, 987, TimeSpan.Zero);
        var windowFrom = watermarkValue.AddMinutes(-5);
        var archivedRows = new[]
        {
            new Row(existing.Id, "Новое имя", true, watermarkValue.AddMinutes(10)),
            new Row(newId, "Новый КА", true, watermarkValue.AddMinutes(20))
        };
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(2, 1, 0, []),
            { Archived: false, Limit: 1000 } => Collection(1, 1000, 0,
                [new Row(activeId, "Активный КА", false, watermarkValue.AddMinutes(5))]),
            { Archived: true, Limit: 1000 } => Collection(2, 1000, 0, archivedRows),
            _ => throw new InvalidOperationException($"Unexpected request: {request}")
        });
        var discovery = new FakeDocumentDiscoveryClient(request => new MoySkladDocumentDiscoveryResponse(
            request.CounterpartyIds.Select(counterpartyId => new MoySkladDocumentReference(
                "purchasereturn", Guid.NewGuid(), counterpartyId)).ToArray(), []));

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo), discovery)
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        Assert.Equal(
            [
                new PageRequest(false, 1, 0, windowFrom, windowTo),
                new PageRequest(true, 1, 0, windowFrom, windowTo),
                new PageRequest(false, 1000, 0, windowFrom, windowTo),
                new PageRequest(true, 1000, 0, windowFrom, windowTo)
            ],
            egress.Requests);
        var discoveryRequest = Assert.Single(discovery.Requests);
        Assert.Equal(new[] { existing.Id, newId }.Order(), discoveryRequest.CounterpartyIds.Order());
        Assert.DoesNotContain(activeId, discoveryRequest.CounterpartyIds);
        var updated = await dbContext.Counterparties.SingleAsync(item => item.Id == existing.Id);
        Assert.Equal(originalId, updated.Id);
        Assert.Equal(originalCreatedAt, updated.CreatedAt);
        Assert.Equal("Новое имя", updated.Name);
        Assert.True(updated.Archived);
        Assert.True(await dbContext.Counterparties.AnyAsync(item => item.Id == newId));
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("incremental", run.RequestedMode);
        Assert.Equal("incremental", run.ExecutionMode);
        Assert.Equal(windowFrom, run.WindowFrom);
        Assert.Equal(windowTo, run.WindowTo);
        Assert.Equal(3, run.TotalCount);
        Assert.Equal(3, run.ProcessedCount);
        Assert.Equal(2, await dbContext.CounterpartyDocuments.CountAsync());
        Assert.Equal(windowTo, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
    }

    [Fact]
    public async Task Incremental_RejectsRowsOutsideWindowAndDoesNotAdvanceWatermark()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var oldWatermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = oldWatermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = oldWatermark
        });
        var existing = Existing(accountId, "Не менять");
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var windowTo = oldWatermark.AddHours(1);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(
                1,
                request.Limit,
                request.Offset,
                [new Row(Guid.NewGuid(), "За границей", false, windowTo)])
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        Assert.Equal(oldWatermark, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
        Assert.Equal(existing.Id, (await dbContext.Counterparties.SingleAsync()).Id);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("failed", run.Status);
        Assert.Equal("COUNTERPARTY_SNAPSHOT_CHANGED", run.ErrorCode);
    }

    [Fact]
    public async Task Incremental_DoesNotOverwriteNewerLocalVersionFromSafetyOverlap()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var watermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = watermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = watermark
        });
        var existing = Existing(accountId, "Более новая локальная версия");
        existing.MoySkladUpdatedAt = watermark.AddMinutes(30);
        existing.Archived = true;
        dbContext.Counterparties.Add(existing);
        var existingDocumentId = Guid.NewGuid();
        dbContext.CounterpartyDocuments.Add(new CounterpartyDocument
        {
            AccountId = accountId,
            CounterpartyId = existing.Id,
            DocumentType = "purchasereturn",
            DocumentId = existingDocumentId,
            UpdatedAt = watermark
        });
        await dbContext.SaveChangesAsync();
        var windowTo = watermark.AddHours(1);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(0, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(1, 1, 0, []),
            _ => Collection(
                1,
                request.Limit,
                request.Offset,
                [new Row(existing.Id, "Старая overlap-версия", true, watermark.AddMinutes(10))])
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        var saved = await dbContext.Counterparties.SingleAsync();
        Assert.Equal("Более новая локальная версия", saved.Name);
        Assert.Equal(watermark.AddMinutes(30), saved.MoySkladUpdatedAt);
        Assert.True(saved.Archived);
        Assert.Equal(existingDocumentId, (await dbContext.CounterpartyDocuments.SingleAsync()).DocumentId);
        Assert.Equal(windowTo, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
        Assert.Equal("completed", (await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical")).Status);
    }

    [Fact]
    public async Task Incremental_DeduplicatesByMoySkladIdUsingNewestUpdatedValue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var watermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = watermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = watermark
        });
        await dbContext.SaveChangesAsync();
        var moySkladId = Guid.NewGuid();
        var windowTo = watermark.AddHours(1);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: false } => Collection(
                1,
                request.Limit,
                request.Offset,
                [new Row(moySkladId, "Ранняя версия", false, watermark.AddMinutes(10))]),
            _ => Collection(
                1,
                request.Limit,
                request.Offset,
                [new Row(moySkladId, "Поздняя версия", true, watermark.AddMinutes(20))])
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        var saved = await dbContext.Counterparties.SingleAsync();
        Assert.Equal("Поздняя версия", saved.Name);
        Assert.True(saved.Archived);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal(2, run.TotalCount);
        Assert.Equal(1, run.ProcessedCount);
    }

    [Fact]
    public async Task Incremental_DuplicateTimestampUsesLaterRowOrder()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var watermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = watermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = watermark
        });
        await dbContext.SaveChangesAsync();
        var id = Guid.NewGuid();
        var updated = watermark.AddMinutes(10);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: false } => Collection(1, request.Limit, request.Offset,
                [new Row(id, "Earlier response row", false, updated)]),
            _ => Collection(1, request.Limit, request.Offset,
                [new Row(id, "Later response row", true, updated)])
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(watermark.AddHours(1)))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        var saved = await dbContext.Counterparties.SingleAsync();
        Assert.Equal("Later response row", saved.Name);
        Assert.True(saved.Archived);
    }

    [Fact]
    public async Task Incremental_WithNoChangesAdvancesWatermarkWithoutChangingCatalog()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = await CreateContextAsync(connection);
        var accountId = Guid.NewGuid();
        var oldWatermark = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        dbContext.SyncWatermarks.Add(new SyncWatermark
        {
            AccountId = accountId,
            Watermark = oldWatermark,
            LastSyncRunId = Guid.NewGuid(),
            UpdatedAt = oldWatermark
        });
        var existing = Existing(accountId, "Без изменений");
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var windowTo = oldWatermark.AddHours(1);
        var egress = new FakeEgressClient(request => Collection(0, request.Limit, request.Offset, []));

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        Assert.Equal(2, egress.Requests.Count);
        Assert.Equal(existing.Id, (await dbContext.Counterparties.SingleAsync()).Id);
        Assert.Equal(windowTo, (await dbContext.SyncWatermarks.SingleAsync()).Watermark);
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("completed", run.Status);
        Assert.Equal(0, run.TotalCount);
        Assert.Equal(0, run.ProcessedCount);
    }

    private static async Task<CatalogSyncDbContext> CreateContextAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new CatalogSyncDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static async Task<CatalogSyncDbContext> CreateContextAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var context = new CatalogSyncDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static SyncProcessor CreateProcessor(
        CatalogSyncDbContext dbContext,
        IMoySkladEgressClient egress,
        TimeProvider? timeProvider = null,
        IMoySkladDocumentDiscoveryClient? discovery = null) =>
        new(
            new SyncRepository(dbContext),
            egress,
            discovery ?? new FakeDocumentDiscoveryClient(_ => new MoySkladDocumentDiscoveryResponse([], [])),
            new MoySkladCounterpartyParser(),
            new CounterpartyNormalizer(),
            timeProvider ?? TimeProvider.System,
            NullLogger<SyncProcessor>.Instance);

    private static SyncRequested Command(Guid accountId, SyncMode mode = SyncMode.Full) =>
        new(Guid.NewGuid(), Guid.NewGuid(), accountId, Guid.NewGuid(), DateTimeOffset.UtcNow, mode);

    private static string Collection(int size, int limit, int offset, IEnumerable<Row> rows) =>
        JsonSerializer.Serialize(new
        {
            meta = new { size, limit, offset },
            rows = rows.Select(row => new
            {
                id = row.Id,
                name = row.Name,
                archived = row.Archived,
                updated = row.Updated?.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd HH:mm:ss.fff")
            })
        });

    private static Counterparty Existing(Guid accountId, string name) =>
        CreateExisting(accountId, name);

    private static Counterparty CreateExisting(Guid accountId, string name)
    {
        var syncRunId = Guid.NewGuid();
        return new Counterparty
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Name = name,
            NormalizedName = name.ToLowerInvariant(),
            LastSyncRunId = syncRunId,
            LastSyncRun = new SyncRun
            {
                Id = syncRunId,
                MessageId = Guid.NewGuid(),
                AccountId = accountId,
                RequestedByUserId = Guid.NewGuid(),
                Status = "historical",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            RawJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed record Row(Guid Id, string Name, bool Archived, DateTimeOffset? Updated = null);
    private sealed record PageRequest(
        bool Archived,
        int Limit,
        int Offset,
        DateTimeOffset? WindowFrom = null,
        DateTimeOffset? WindowTo = null);

    private sealed record DiscoveryRequest(Guid AccountId, Guid[] CounterpartyIds, Guid UserId, string CorrelationId);

    private sealed class FakeDocumentDiscoveryClient(
        Func<DiscoveryRequest, MoySkladDocumentDiscoveryResponse> responder) : IMoySkladDocumentDiscoveryClient
    {
        public List<DiscoveryRequest> Requests { get; } = [];

        public Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
            Guid accountId,
            IReadOnlyList<Guid> counterpartyIds,
            Guid userId,
            string correlationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new DiscoveryRequest(accountId, counterpartyIds.ToArray(), userId, correlationId);
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeEgressClient(Func<PageRequest, string> responder) : IMoySkladEgressClient
    {
        public List<PageRequest> Requests { get; } = [];

        public Task<MoySkladConnectionCheckResponse> CheckConnectionAsync(
            Guid accountId,
            string correlationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MoySkladConnectionCheckResponse(true));

        public Task<MoySkladRawResponse> GetCounterpartiesAsync(
            Guid accountId,
            bool archived,
            int limit,
            int offset,
            DateTimeOffset? windowFrom,
            DateTimeOffset? windowTo,
            Guid syncRunId,
            Guid userId,
            string correlationId,
            CancellationToken cancellationToken)
        {
            var request = new PageRequest(archived, limit, offset, windowFrom, windowTo);
            Requests.Add(request);
            return Task.FromResult(new MoySkladRawResponse(responder(request), 200, "application/json"));
        }
    }

    private sealed class BlockingEgressClient(
        Func<PageRequest, CancellationToken, Task<string>> responder) : IMoySkladEgressClient
    {
        public Task<MoySkladConnectionCheckResponse> CheckConnectionAsync(
            Guid accountId,
            string correlationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MoySkladConnectionCheckResponse(true));

        public async Task<MoySkladRawResponse> GetCounterpartiesAsync(
            Guid accountId,
            bool archived,
            int limit,
            int offset,
            DateTimeOffset? windowFrom,
            DateTimeOffset? windowTo,
            Guid syncRunId,
            Guid userId,
            string correlationId,
            CancellationToken cancellationToken)
        {
            var request = new PageRequest(archived, limit, offset, windowFrom, windowTo);
            var json = await responder(request, cancellationToken);
            return new MoySkladRawResponse(json, 200, "application/json");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
