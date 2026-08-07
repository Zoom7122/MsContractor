using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergeJobCreatorTests
{
    [Fact]
    public async Task CreateAsync_StoresJobOperationsAndOutboxAtomically()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Duplicate.Archived = true;
        await fixture.Db.SaveChangesAsync();

        var accepted = await fixture.Creator.CreateAsync(
            fixture.AccountId, Guid.NewGuid(), Guid.NewGuid(), fixture.Request(), CancellationToken.None);

        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(accepted.MergeJobId, job.Id);
        Assert.Equal(MergeJobStatuses.Pending, job.Status);
        Assert.Equal(2, job.Operations.Count);
        Assert.All(job.Operations, operation => Assert.Equal(MergeOperationStatuses.Pending, operation.Status));
        var archive = job.Operations.Single(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate);
        Assert.Equal(fixture.Duplicate.Id, archive.CounterpartyId);
        var outbox = await fixture.Db.OutboxMessages.SingleAsync();
        Assert.Equal(nameof(MergeRequested), outbox.EventType);
        Assert.Equal(fixture.AccountId.ToString("D"), outbox.MessageKey);
        Assert.Equal(job.MessageId, outbox.Id);
        Assert.NotNull(JsonSerializer.Deserialize<MergeRequested>(outbox.Payload, JsonOptions));
    }

    [Fact]
    public async Task CreateAsync_RejectsDuplicateIdsAndMainAmongDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync();
        var repeated = fixture.Request() with
        {
            DuplicateCounterpartyIds = [fixture.Duplicate.Id, fixture.Duplicate.Id]
        };
        var includesMain = fixture.Request() with
        {
            DuplicateCounterpartyIds = [fixture.Main.Id]
        };

        var repeatedError = await Assert.ThrowsAsync<MergeRequestException>(() => fixture.Creator.CreateAsync(
            fixture.AccountId, Guid.NewGuid(), Guid.NewGuid(), repeated, CancellationToken.None));
        var mainError = await Assert.ThrowsAsync<MergeRequestException>(() => fixture.Creator.CreateAsync(
            fixture.AccountId, Guid.NewGuid(), Guid.NewGuid(), includesMain, CancellationToken.None));

        Assert.Equal(MergeRequestError.Invalid, repeatedError.Error);
        Assert.Equal(MergeRequestError.Invalid, mainError.Error);
        Assert.Empty(fixture.Db.MergeJobs);
    }

    [Fact]
    public async Task CreateAsync_HidesCrossAccountCounterpartyAsNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherAccount = Guid.NewGuid();
        var otherRun = NewRun(otherAccount);
        var foreign = NewCounterparty(otherAccount, otherRun, "Foreign");
        fixture.Db.AddRange(otherRun, foreign);
        await fixture.Db.SaveChangesAsync();
        var request = fixture.Request() with { DuplicateCounterpartyIds = [foreign.Id] };

        var exception = await Assert.ThrowsAsync<MergeRequestException>(() => fixture.Creator.CreateAsync(
            fixture.AccountId, Guid.NewGuid(), Guid.NewGuid(), request, CancellationToken.None));

        Assert.Equal(MergeRequestError.NotFound, exception.Error);
    }

    [Fact]
    public async Task CreateAsync_RejectsArchivedMain()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Main.Archived = true;
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<MergeRequestException>(() => fixture.Creator.CreateAsync(
            fixture.AccountId, Guid.NewGuid(), Guid.NewGuid(), fixture.Request(), CancellationToken.None));

        Assert.Equal(MergeRequestError.MainArchived, exception.Error);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class Fixture(SqliteConnection connection, CatalogSyncDbContext db) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public CatalogSyncDbContext Db { get; } = db;
        public Guid AccountId { get; } = Guid.NewGuid();
        public Counterparty Main { get; private set; } = null!;
        public Counterparty Duplicate { get; private set; } = null!;
        public MergeJobCreator Creator => new(Db, TimeProvider.System);

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            var run = NewRun(fixture.AccountId);
            fixture.Main = NewCounterparty(fixture.AccountId, run, "Main");
            fixture.Duplicate = NewCounterparty(fixture.AccountId, run, "Duplicate");
            db.AddRange(run, fixture.Main, fixture.Duplicate);
            await db.SaveChangesAsync();
            return fixture;
        }

        public CreateMergeJobRequest Request() => new(
            Main.Id,
            [Duplicate.Id],
            new MergeMainCounterpartyDto("Updated Main", "new@example.test", "+79991234567", null));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
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
