using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Sync;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MsContractor.Sync.Tests;

public sealed class SyncRunErrorsPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container;

    public SyncRunErrorsPostgresFixture()
    {
        _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("sync_errors_tests")
            .WithUsername("testadmin")
            .WithPassword(Guid.NewGuid().ToString("N"))
            .Build();
    }

    public CatalogSyncDbContext CreateContext() => new(new DbContextOptionsBuilder<CatalogSyncDbContext>()
        .UseNpgsql(_container.GetConnectionString())
        .AddInterceptors(new CatalogTenantConnectionInterceptor()).Options);

    public NpgsqlConnection CreateConnection() => new(_container.GetConnectionString());

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE ROLE sync_errors_reader NOLOGIN;
            GRANT USAGE ON SCHEMA catalog_sync TO sync_errors_reader;
            GRANT SELECT, INSERT ON catalog_sync.sync_runs_error TO sync_errors_reader;
            """);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}

public sealed class SyncRunErrorsPostgresTests : IClassFixture<SyncRunErrorsPostgresFixture>
{
    private readonly SyncRunErrorsPostgresFixture _fixture;

    public SyncRunErrorsPostgresTests(SyncRunErrorsPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JournalAndCounter_DeduplicateAcrossRestartAndSurviveCompletionWithStaleRun()
    {
        await using var db = _fixture.CreateContext();
        var (run, owner) = await SeedRunAsync(db, 1);
        var repository = new SyncRepository(db);
        var row = Row(run, 0, "invalid");
        row.IsValidForStorage = false;
        var error = SyncRunError.Create(run.AccountId, run.Id, row.CounterpartyId,
            "COUNTERPARTY_FIELD_TOO_LONG", "Поле Name слишком длинное.", run.CreatedAt, "Name");

        await repository.StagePageAsync(run.AccountId, run.Id, owner, [row], false, run.CreatedAt,
            Lease(), CancellationToken.None, [error, error]);
        Assert.Equal(1, await CountAsync(db, run));
        await repository.RestartStagingAsync(run.AccountId, run.Id, owner, run.CreatedAt, run.WindowTo!.Value,
            Lease(), CancellationToken.None);
        await repository.StagePageAsync(run.AccountId, run.Id, owner, [row], false, run.CreatedAt,
            Lease(), CancellationToken.None, [error]);
        Assert.Equal(1, await CountAsync(db, run));
        Assert.Equal(0, run.ErrorsCount); // This snapshot deliberately predates the journal writes.

        await repository.CompleteFullFromStagingAsync(run.AccountId, run.Id, owner, [], [],
            Completion(run), Lease(), CancellationToken.None);
        var saved = await db.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id);
        Assert.Equal("completed", saved.Status);
        Assert.Equal(1, saved.ErrorsCount);
        Assert.Equal(0, saved.ProcessedCount);
        Assert.Single(await ErrorsAsync(db, run));
    }

    [Fact]
    public async Task Staging_IsolatesInvalidTextAndPreservesSnapshotCardinality()
    {
        await using var db = _fixture.CreateContext();
        var (run, owner) = await SeedRunAsync(db, 2);
        var good = Row(run, 0, "valid");
        var bad = Row(run, 1, "invalid\0text");
        var repository = new SyncRepository(db);

        await repository.StagePageAsync(run.AccountId, run.Id, owner, [good, bad], false,
            run.CreatedAt, Lease(), CancellationToken.None);
        var staged = await db.CounterpartySyncStaging.AsNoTracking().Where(item => item.SyncRunId == run.Id)
            .OrderBy(item => item.Sequence).ToArrayAsync();
        Assert.Equal(2, staged.Length);
        Assert.True(staged[0].IsValidForStorage);
        Assert.False(staged[1].IsValidForStorage);
        Assert.Equal(bad.CounterpartyId, staged[1].CounterpartyId);
        Assert.Equal(1, await CountAsync(db, run));
        Assert.Equal(bad.CounterpartyId, Assert.Single(await ErrorsAsync(db, run)).CounterpartyId);

        await repository.CompleteFullFromStagingAsync(run.AccountId, run.Id, owner, [], [],
            Completion(run), Lease(), CancellationToken.None);
        Assert.Equal(good.CounterpartyId,
            (await db.Counterparties.AsNoTracking().SingleAsync(item => item.AccountId == run.AccountId)).Id);
        Assert.Equal(1, (await db.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id)).ProcessedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_IsolatesJsonbFailurePreservesOtherWritesAndFiltersDocuments(bool incremental)
    {
        await using var db = _fixture.CreateContext();
        var (run, owner) = await SeedRunAsync(db, 2, incremental);
        var good = Row(run, 0, "valid");
        var bad = Row(run, 1, "new bad");
        good.Archived = bad.Archived = true;
        bad.RawJson = "{\"unsupported\":\"\\u0000\"}";
        var oldDocument = Document(run, bad.CounterpartyId);
        if (incremental)
        {
            db.Counterparties.Add(Counterparty(run, bad.CounterpartyId, "old bad"));
            db.CounterpartyDocuments.Add(oldDocument);
            await db.SaveChangesAsync();
        }
        var goodDocument = Document(run, good.CounterpartyId);
        var badDocument = Document(run, bad.CounterpartyId);
        var commissions = new[] { goodDocument, badDocument }.Select(item => new DocumentAdditionalCommission
        {
            DocumentId = item.DocumentId, Contract = Guid.NewGuid()
        }).ToArray();
        var repository = new SyncRepository(db);
        await repository.StagePageAsync(run.AccountId, run.Id, owner, [good, bad], incremental,
            run.CreatedAt, Lease(), CancellationToken.None);
        var completion = Completion(run);
        if (incremental)
            await repository.CompleteIncrementalFromStagingAsync(run.AccountId, run.Id, owner,
                [goodDocument, badDocument], commissions, completion, Apply, Lease(), CancellationToken.None);
        else
            await repository.CompleteFullFromStagingAsync(run.AccountId, run.Id, owner,
                [goodDocument, badDocument], commissions, completion, Lease(), CancellationToken.None);

        var saved = await db.Counterparties.AsNoTracking().Where(item => item.AccountId == run.AccountId).ToArrayAsync();
        Assert.Contains(saved, item => item.Id == good.CounterpartyId);
        if (incremental) Assert.Equal("old bad", Assert.Single(saved, item => item.Id == bad.CounterpartyId).Name);
        else Assert.DoesNotContain(saved, item => item.Id == bad.CounterpartyId);
        var documents = await db.CounterpartyDocuments.AsNoTracking().Where(item => item.AccountId == run.AccountId).ToArrayAsync();
        Assert.Contains(documents, item => item.DocumentId == goodDocument.DocumentId);
        Assert.DoesNotContain(documents, item => item.DocumentId == badDocument.DocumentId);
        Assert.Equal(incremental, documents.Any(item => item.DocumentId == oldDocument.DocumentId));
        Assert.False(await db.DocumentAdditionalCommissions.AnyAsync(item => item.DocumentId == badDocument.DocumentId));
        Assert.True(await db.DocumentAdditionalCommissions.AnyAsync(item => item.DocumentId == goodDocument.DocumentId));
        var storedRun = await db.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id);
        Assert.Equal("completed", storedRun.Status);
        Assert.Equal(1, storedRun.ErrorsCount);
        Assert.Equal(1, storedRun.ProcessedCount);
        Assert.Equal(bad.CounterpartyId, Assert.Single(await ErrorsAsync(db, run)).CounterpartyId);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(item => item.MessageKey == run.AccountId.ToString("D"));
        Assert.Equal(1, JsonSerializer.Deserialize<SyncCompleted>(outbox.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ProcessedCount);
    }

    [Fact]
    public async Task FatalPublicationFailure_RollsBackJournalAndCounterThenPersistsDiagnosticsWithFailure()
    {
        await using var db = _fixture.CreateContext();
        var (run, owner) = await SeedRunAsync(db, 2);
        var oldId = Guid.NewGuid();
        db.Counterparties.Add(Counterparty(run, oldId, "old snapshot"));
        var oldWatermark = run.CreatedAt.AddHours(-1);
        db.SyncWatermarks.Add(new SyncWatermark { AccountId = run.AccountId, LastSyncRunId = run.Id, Watermark = oldWatermark });
        await db.SaveChangesAsync();
        var good = Row(run, 0, "new valid");
        var bad = Row(run, 1, "new bad");
        bad.RawJson = "{\"unsupported\":\"\\u0000\"}";
        var repository = new SyncRepository(db);
        await repository.StagePageAsync(run.AccountId, run.Id, owner, [good, bad], false,
            run.CreatedAt, Lease(), CancellationToken.None);
        var invalidCompletion = Completion(run) with { CreateCompletedOutbox = null };
        invalidCompletion.Outbox.Payload = null!;

        await Assert.ThrowsAsync<CatalogPersistenceException>(() => repository.CompleteFullFromStagingAsync(
            run.AccountId, run.Id, owner, [], [], invalidCompletion, Lease(), CancellationToken.None));
        db.ChangeTracker.Clear();
        Assert.Equal(0, await CountAsync(db, run));
        Assert.Empty(await ErrorsAsync(db, run));
        Assert.Equal(oldId, (await db.Counterparties.SingleAsync(item => item.AccountId == run.AccountId)).Id);
        Assert.Equal(oldWatermark, (await db.SyncWatermarks.SingleAsync(item => item.AccountId == run.AccountId)).Watermark);

        var failed = await repository.FailAsync(run.AccountId, run.Id, owner, "COUNTERPARTY_SAVE_FAILED",
            "Не удалось завершить сохранение каталога.", DateTimeOffset.UtcNow,
            new InboxMessage { MessageId = run.MessageId, ConsumerName = "postgres-sync-errors" },
            Outbox(run, "{}"), CancellationToken.None);
        Assert.True(failed);
        Assert.Equal(2, await CountAsync(db, run));
        var errors = await ErrorsAsync(db, run);
        Assert.Equal(2, errors.Length);
        Assert.Contains(errors, error => error.CounterpartyId == bad.CounterpartyId);
        Assert.Contains(errors, error => error.CounterpartyId is null);
        Assert.Empty(await db.CounterpartySyncStaging.Where(item => item.SyncRunId == run.Id).ToArrayAsync());
        Assert.Equal("failed", (await db.SyncRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id)).Status);
        Assert.Equal(oldWatermark, (await db.SyncWatermarks.SingleAsync(item => item.AccountId == run.AccountId)).Watermark);
    }

    [Fact]
    public async Task HistoryAndErrors_AreAccountScopedAndUseStoredCounterForPagination()
    {
        await using var db = _fixture.CreateContext();
        var (run, _) = await SeedRunAsync(db, 0);
        var foreign = new SyncRun { Id = Guid.NewGuid(), AccountId = Guid.NewGuid(), MessageId = Guid.NewGuid() };
        db.SyncRuns.Add(foreign);
        for (var index = 0; index < 30; index++)
            db.SyncRunErrors.Add(SyncRunError.Create(run.AccountId, run.Id, Guid.NewGuid(), "TEST_ERROR", "Ошибка", run.CreatedAt.AddSeconds(index)));
        await db.SaveChangesAsync();
        await db.SyncRuns.Where(item => item.Id == run.Id).ExecuteUpdateAsync(update => update.SetProperty(item => item.ErrorsCount, 30));
        var repository = new SyncRunHistoryRepository(db);
        var history = await repository.GetAsync(run.AccountId, 1, 25, CancellationToken.None);
        Assert.Equal(1, history.Total);
        Assert.Equal(30, Assert.Single(history.Items).ErrorsCount);
        var errors = await repository.GetErrorsAsync(run.AccountId, run.Id, 2, 25, CancellationToken.None);
        Assert.NotNull(errors);
        Assert.Equal(30, errors.Total);
        Assert.Equal(5, errors.Items.Count);
        Assert.True(errors.Items.Zip(errors.Items.Skip(1), (a, b) => a.CreatedAt >= b.CreatedAt).All(value => value));
        Assert.Null(await repository.GetErrorsAsync(run.AccountId, foreign.Id, 1, 25, CancellationToken.None));
        Assert.Null(await repository.GetErrorsAsync(run.AccountId, Guid.NewGuid(), 1, 25, CancellationToken.None));
    }

    [Fact]
    public async Task CompositeForeignKeyAndRls_RejectCrossAccountRowsAndHideOtherAccounts()
    {
        await using var db = _fixture.CreateContext();
        var (run, _) = await SeedRunAsync(db, 0);
        db.SyncRunErrors.Add(SyncRunError.Create(run.AccountId, run.Id, null, "TEST_ERROR", "Ошибка", run.CreatedAt));
        await db.SaveChangesAsync();
        db.SyncRunErrors.Add(SyncRunError.Create(Guid.NewGuid(), run.Id, null, "TEST_ERROR", "Ошибка", run.CreatedAt));
        var foreignKey = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("23503", Assert.IsType<PostgresException>(foreignKey.InnerException).SqlState);
        db.ChangeTracker.Clear();

        await using var connection = _fixture.CreateConnection();
        await connection.OpenAsync();
        await using (var role = new NpgsqlCommand("SET ROLE sync_errors_reader", connection)) await role.ExecuteNonQueryAsync();
        await using (var tenant = new NpgsqlCommand("SELECT set_config('app.account_id', @account, false)", connection))
        {
            tenant.Parameters.AddWithValue("account", run.AccountId.ToString("D"));
            await tenant.ExecuteNonQueryAsync();
        }
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM catalog_sync.sync_runs_error", connection))
            Assert.Equal(1L, await read.ExecuteScalarAsync());
        await using (var write = new NpgsqlCommand("""
            INSERT INTO catalog_sync.sync_runs_error
                ("Id", "AccountId", "SyncRunId", "ErrorMessage", "ReasonCode", "ErrorKey", "CreatedAt")
            VALUES (@id, @account, @run, 'Ошибка', 'TEST_ERROR', @key, @now)
            """, connection))
        {
            write.Parameters.AddWithValue("id", Guid.NewGuid());
            write.Parameters.AddWithValue("account", Guid.NewGuid());
            write.Parameters.AddWithValue("run", run.Id);
            write.Parameters.AddWithValue("key", Guid.NewGuid().ToString("N"));
            write.Parameters.AddWithValue("now", run.CreatedAt);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => write.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState);
        }
        await using (var reset = new NpgsqlCommand("RESET app.account_id", connection)) await reset.ExecuteNonQueryAsync();
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM catalog_sync.sync_runs_error", connection))
            Assert.Equal(0L, await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LostLeaseAndCancellation_DoNotWriteJournalOrCounter()
    {
        await using var db = _fixture.CreateContext();
        var (run, owner) = await SeedRunAsync(db, 1);
        var row = Row(run, 0, "valid");
        var error = SyncRunError.Create(run.AccountId, run.Id, row.CounterpartyId, "TEST_ERROR", "Ошибка", run.CreatedAt);
        var repository = new SyncRepository(db);
        await Assert.ThrowsAsync<SyncRunLeaseLostException>(() => repository.StagePageAsync(run.AccountId, run.Id,
            Guid.NewGuid(), [row], false, run.CreatedAt, Lease(), CancellationToken.None, [error]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.StagePageAsync(run.AccountId, run.Id,
            owner, [row], false, run.CreatedAt, Lease(), cancellation.Token, [error]));
        Assert.Equal(0, await CountAsync(db, run));
        Assert.Empty(await ErrorsAsync(db, run));
    }

    private static async Task<(SyncRun Run, Guid Owner)> SeedRunAsync(CatalogSyncDbContext db, int total, bool incremental = false)
    {
        var owner = Guid.NewGuid();
        var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var run = new SyncRun
        {
            Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), AccountId = Guid.NewGuid(), RequestedByUserId = Guid.NewGuid(),
            Status = "running", ExecutionMode = incremental ? "incremental" : "full", TotalCount = total,
            ProcessingOwnerToken = owner, ProcessingLeaseExpiresAtTicks = Lease(),
            CreatedAt = now, UpdatedAt = now, StartedAt = now, WindowTo = now
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        return (run, owner);
    }

    private static CounterpartySyncStage Row(SyncRun run, long sequence, string name) => new()
    {
        AccountId = run.AccountId, SyncRunId = run.Id, CounterpartyId = Guid.NewGuid(), Sequence = sequence,
        IsValidForStorage = true, Name = name, NormalizedName = name, LastSyncRunId = run.Id,
        RawJson = "{}", CreatedAt = run.CreatedAt, UpdatedAt = run.CreatedAt
    };

    private static Counterparty Counterparty(SyncRun run, Guid id, string name) => new()
    {
        Id = id, AccountId = run.AccountId, Name = name, NormalizedName = name,
        RawJson = "{}", LastSyncRunId = run.Id, CreatedAt = run.CreatedAt, UpdatedAt = run.CreatedAt
    };

    private static CounterpartyDocument Document(SyncRun run, Guid id) => new()
    {
        AccountId = run.AccountId, CounterpartyId = id, DocumentId = Guid.NewGuid(),
        DocumentType = "commissionreportin", UpdatedAt = run.CreatedAt
    };

    private static bool Apply(Counterparty target, Counterparty source)
    {
        target.Name = source.Name;
        target.NormalizedName = source.NormalizedName;
        target.Archived = source.Archived;
        target.RawJson = source.RawJson;
        target.LastSyncRunId = source.LastSyncRunId;
        return true;
    }

    private static SyncCompletion Completion(SyncRun run)
    {
        run.Status = "completed";
        run.CompletedAt = DateTimeOffset.UtcNow;
        return new SyncCompletion(run,
            new InboxMessage { MessageId = run.MessageId, ConsumerName = "postgres-sync-errors" }, Outbox(run, "{}"),
            new SyncWatermark { AccountId = run.AccountId, LastSyncRunId = run.Id, Watermark = run.WindowTo!.Value },
            count => Outbox(run, JsonSerializer.Serialize(new SyncCompleted(Guid.NewGuid(), run.Id,
                run.AccountId, count, DateTimeOffset.UtcNow), new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }

    private static SyncOutboxMessage Outbox(SyncRun run, string payload) => new()
    {
        Id = Guid.NewGuid(), Topic = SyncTopics.Events,
        MessageKey = run.AccountId.ToString("D"), EventType = "test", Payload = payload, CreatedAt = DateTimeOffset.UtcNow
    };

    private static long Lease() => DateTimeOffset.UtcNow.AddMinutes(5).UtcTicks;
    private static Task<int> CountAsync(CatalogSyncDbContext db, SyncRun run) => db.SyncRuns.AsNoTracking()
        .Where(item => item.AccountId == run.AccountId && item.Id == run.Id).Select(item => item.ErrorsCount).SingleAsync();
    private static Task<SyncRunError[]> ErrorsAsync(CatalogSyncDbContext db, SyncRun run) => db.SyncRunErrors.AsNoTracking()
        .Where(item => item.AccountId == run.AccountId && item.SyncRunId == run.Id).ToArrayAsync();
}
