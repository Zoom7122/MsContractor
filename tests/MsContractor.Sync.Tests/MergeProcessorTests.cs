using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergeProcessorTests
{
    [Fact]
    public async Task ProcessAsync_UpdatesMainArchivesLocallyArchivedDuplicateAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        fixture.Duplicates[0].Archived = true;
        await fixture.Db.SaveChangesAsync();
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var main = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Main.Id);
        var duplicate = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Duplicates[0].Id);
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal("Updated Main", main.Name);
        Assert.Equal("updated main", main.NormalizedName);
        Assert.Equal("new@example.test", main.NormalizedEmail);
        Assert.Equal("+79991234567", main.NormalizedPhone);
        Assert.True(duplicate.Archived);
        Assert.Equal(MergeJobStatuses.Completed, job.Status);
        Assert.Single(fixture.Egress.ArchiveCalls);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Single(fixture.Egress.UpdateCalls);
        Assert.Single(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_UpdateFailureDoesNotChangeMainOrArchiveDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.UpdateException = new MergeEgressException(
            "MOYSKLAD_VALIDATION_FAILED", "MoySklad rejected the counterparty update.", 400);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var main = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Main.Id);
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal("Main", main.Name);
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty).Status);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_ArchiveFailureContinuesAndProducesPartialCompletion()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 2);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.ArchiveExceptions[fixture.Duplicates[0].Id] = new MergeEgressException(
            "MOYSKLAD_FORBIDDEN", "MoySklad denied access.", 403);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var duplicates = await fixture.Db.Counterparties
            .Where(item => fixture.Duplicates.Select(duplicate => duplicate.Id).Contains(item.Id))
            .OrderBy(item => item.Name)
            .ToListAsync();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.False(duplicates.Single(item => item.Id == fixture.Duplicates[0].Id).Archived);
        Assert.True(duplicates.Single(item => item.Id == fixture.Duplicates[1].Id).Archived);
        Assert.Equal(2, fixture.Egress.ArchiveCalls.Count);
        Assert.Equal(MergeJobStatuses.PartiallyCompleted, job.Status);
        Assert.Single(job.Operations, item => item.Status == MergeOperationStatuses.Failed);
    }

    [Fact]
    public async Task ProcessAsync_RetryableFailurePersistsAttemptAndDoesNotCompleteJob()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1, maxAttempts: 2);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.UpdateException = new MergeEgressException(
            "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", 503);

        await Assert.ThrowsAsync<MergeRetryableException>(() =>
            fixture.Processor.ProcessAsync(command, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var update = job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty);
        Assert.Equal(MergeJobStatuses.Running, job.Status);
        Assert.Equal(MergeOperationStatuses.Pending, update.Status);
        Assert.Equal(1, update.AttemptCount);
        Assert.Empty(await fixture.Db.InboxMessages.ToListAsync());
    }

    private sealed class Fixture(
        SqliteConnection connection,
        CatalogSyncDbContext db,
        FakeMergeEgressClient egress,
        int maxAttempts) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public SqliteConnection Connection { get; } = connection;
        public CatalogSyncDbContext Db { get; } = db;
        public FakeMergeEgressClient Egress { get; } = egress;
        public Guid AccountId { get; } = Guid.NewGuid();
        public Counterparty Main { get; private set; } = null!;
        public List<Counterparty> Duplicates { get; } = [];
        public MergeProcessor Processor => new(
            Db, Egress, new MoySkladCounterpartyParser(), new CounterpartyNormalizer(),
            TimeProvider.System,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Merge:MaxOperationAttempts"] = maxAttempts.ToString()
            }).Build(),
            NullLogger<MergeProcessor>.Instance);

        public static async Task<Fixture> CreateAsync(int duplicateCount, int maxAttempts = 5)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var egress = new FakeMergeEgressClient();
            var fixture = new Fixture(connection, db, egress, maxAttempts);
            var run = NewRun(fixture.AccountId);
            fixture.Main = NewCounterparty(fixture.AccountId, run, "Main");
            db.Add(run);
            db.Add(fixture.Main);
            for (var index = 0; index < duplicateCount; index++)
            {
                var duplicate = NewCounterparty(fixture.AccountId, run, $"Duplicate {index}");
                fixture.Duplicates.Add(duplicate);
                db.Add(duplicate);
            }
            await db.SaveChangesAsync();
            egress.Names[fixture.Main.Id] = "Updated Main";
            foreach (var duplicate in fixture.Duplicates)
                egress.Names[duplicate.Id] = duplicate.Name;
            return fixture;
        }

        public async Task<MergeRequested> CreateJobAsync()
        {
            var creator = new MergeJobCreator(Db, TimeProvider.System);
            await creator.CreateAsync(
                AccountId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new CreateMergeJobRequest(
                    Main.Id,
                    Duplicates.Select(item => item.Id).ToArray(),
                    new MergeMainCounterpartyDto(
                        "Updated Main", "new@example.test", "+79991234567", "confirmed")),
                CancellationToken.None);
            var payload = await Db.OutboxMessages.Select(item => item.Payload).SingleAsync();
            Db.ChangeTracker.Clear();
            return JsonSerializer.Deserialize<MergeRequested>(payload, JsonOptions)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeMergeEgressClient : IMergeEgressClient
    {
        public List<Guid> UpdateCalls { get; } = [];
        public List<Guid> ArchiveCalls { get; } = [];
        public Dictionary<Guid, string> Names { get; } = [];
        public Dictionary<Guid, Exception> ArchiveExceptions { get; } = [];
        public Exception? UpdateException { get; set; }

        public Task<MergeEgressResponse> UpdateAsync(
            Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add(counterpartyId);
            if (UpdateException is not null)
                return Task.FromException<MergeEgressResponse>(UpdateException);
            return Task.FromResult(new MergeEgressResponse(Json(
                counterpartyId, update.Name, update.Email, update.Phone, update.Description, false)));
        }

        public Task<MergeEgressResponse> ArchiveAsync(
            Guid accountId, Guid counterpartyId, Guid mergeJobId, Guid operationId,
            Guid userId, Guid correlationId, CancellationToken cancellationToken)
        {
            ArchiveCalls.Add(counterpartyId);
            if (ArchiveExceptions.TryGetValue(counterpartyId, out var exception))
                return Task.FromException<MergeEgressResponse>(exception);
            return Task.FromResult(new MergeEgressResponse(Json(
                counterpartyId, Names[counterpartyId], null, null, null, true)));
        }

        private static string Json(
            Guid id, string name, string? email, string? phone, string? description, bool archived) =>
            JsonSerializer.Serialize(new { id, name, email, phone, description, archived, updated = "2026-08-07 12:00:00.000" });
    }

    private static SyncRun NewRun(Guid accountId) => new()
    {
        Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), AccountId = accountId,
        RequestedByUserId = Guid.NewGuid(), RequestedMode = "full", ExecutionMode = "full",
        Status = "completed", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Counterparty NewCounterparty(Guid accountId, SyncRun run, string name) => new()
    {
        Id = Guid.NewGuid(), AccountId = accountId, Name = name,
        NormalizedName = name.ToLowerInvariant(), RawJson = "{}",
        LastSyncRun = run, LastSyncRunId = run.Id,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}
