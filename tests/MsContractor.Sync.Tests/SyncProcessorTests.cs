using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Sync;

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
                DocumentType = "salesreturn",
                DocumentId = Guid.NewGuid(),
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new CounterpartyDocument
            {
                AccountId = otherAccountId,
                CounterpartyId = existingOther.Id,
                DocumentType = "salesreturn",
                DocumentId = Guid.NewGuid(),
                UpdatedAt = DateTimeOffset.UtcNow
            });
        await dbContext.SaveChangesAsync();

        var moySkladId = Guid.NewGuid();
        var catalogWasEmptyAtFirstRequest = false;
        var egress = new FakeEgressClient(request =>
        {
            if (request == new PageRequest(false, 1, 0))
                catalogWasEmptyAtFirstRequest = !dbContext.Counterparties.Any(item => item.AccountId == accountId);
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
        Assert.True(catalogWasEmptyAtFirstRequest);
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
    public async Task FullSync_LeavesAccountCatalogEmptyForIncompleteOrDuplicateSnapshot(bool duplicate)
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

        Assert.Empty(await dbContext.Counterparties.Where(item => item.AccountId == accountId).ToListAsync());
        var run = await dbContext.SyncRuns.SingleAsync(item => item.Status != "historical");
        Assert.Equal("failed", run.Status);
        Assert.Equal("COUNTERPARTY_SNAPSHOT_CHANGED", run.ErrorCode);
    }

    [Fact]
    public async Task FullSync_LeavesAccountCatalogEmptyWhenMetaSizeChanges()
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

        Assert.Empty(await dbContext.Counterparties.Where(item => item.AccountId == accountId).ToListAsync());
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
        var windowTo = new DateTimeOffset(2026, 3, 20, 10, 0, 0, 987, TimeSpan.Zero);
        var windowFrom = watermarkValue.AddMinutes(-5);
        var archivedRows = new[]
        {
            new Row(existing.Id, "Новое имя", true, watermarkValue.AddMinutes(10)),
            new Row(newId, "Новый КА", true, watermarkValue.AddMinutes(20))
        };
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(0, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(2, 1, 0, []),
            { Archived: true, Limit: 1000 } => Collection(2, 1000, 0, archivedRows),
            _ => throw new InvalidOperationException($"Unexpected request: {request}")
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        Assert.Equal(
            [
                new PageRequest(false, 1, 0, windowFrom, windowTo),
                new PageRequest(true, 1, 0, windowFrom, windowTo),
                new PageRequest(true, 1000, 0, windowFrom, windowTo)
            ],
            egress.Requests);
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
        Assert.Equal(2, run.TotalCount);
        Assert.Equal(2, run.ProcessedCount);
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
        dbContext.Counterparties.Add(existing);
        await dbContext.SaveChangesAsync();
        var windowTo = watermark.AddHours(1);
        var egress = new FakeEgressClient(request => request switch
        {
            { Archived: false, Limit: 1 } => Collection(1, 1, 0, []),
            { Archived: true, Limit: 1 } => Collection(0, 1, 0, []),
            _ => Collection(
                1,
                request.Limit,
                request.Offset,
                [new Row(existing.Id, "Старая overlap-версия", false, watermark.AddMinutes(10))])
        });

        await CreateProcessor(dbContext, egress, new FixedTimeProvider(windowTo))
            .ProcessAsync(Command(accountId, SyncMode.Incremental), CancellationToken.None);

        var saved = await dbContext.Counterparties.SingleAsync();
        Assert.Equal("Более новая локальная версия", saved.Name);
        Assert.Equal(watermark.AddMinutes(30), saved.MoySkladUpdatedAt);
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

    private static SyncProcessor CreateProcessor(
        CatalogSyncDbContext dbContext,
        IMoySkladEgressClient egress,
        TimeProvider? timeProvider = null) =>
        new(
            dbContext,
            egress,
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

    private sealed class FakeEgressClient(Func<PageRequest, string> responder) : IMoySkladEgressClient
    {
        public List<PageRequest> Requests { get; } = [];

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

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
