using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Repositories;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.Sync.Tests;

public sealed class RepositoryBoundaryTests
{
    [Fact]
    public async Task MergeCreation_RollsBackJobAndOperationsWhenOutboxCannotBeSaved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = new MergeJob
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            MessageId = Guid.NewGuid(),
            MainCounterpartyId = fixture.Counterparty.Id,
            Payload = "{}"
        };
        job.Operations.Add(new MergeOperation
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            MergeJobId = job.Id,
            CounterpartyId = fixture.Counterparty.Id,
            OperationType = "test",
            Status = "pending"
        });
        var outbox = Outbox();
        outbox.Payload = null!;
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Merge.CreateAsync(
            fixture.AccountId, job, outbox, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.MergeJobs.ToListAsync());
        Assert.Empty(await fixture.Db.MergeOperations.ToListAsync());
        Assert.Empty(await fixture.Db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task SyncCompletion_RollsBackRowsRunInboxAndWatermarkWhenOutboxCannotBeSaved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = fixture.Counterparty.LastSyncRun;
        var oldStatus = run.Status;
        run.Status = "completed";
        var outbox = Outbox();
        outbox.Payload = null!;
        var completion = new SyncCompletion(run,
            new InboxMessage { MessageId = run.MessageId, ConsumerName = "test" }, outbox,
            new SyncWatermark { AccountId = fixture.AccountId, LastSyncRunId = run.Id, Watermark = DateTimeOffset.UtcNow });
        var incoming = new Counterparty
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            LastSyncRunId = run.Id,
            Name = "new"
        };
        await Assert.ThrowsAsync<CatalogPersistenceException>(() => fixture.Sync.CompleteFullAsync(
            fixture.AccountId, [incoming], completion, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(oldStatus, (await fixture.Db.SyncRuns.SingleAsync()).Status);
        Assert.Single(await fixture.Db.Counterparties.ToListAsync());
        Assert.Empty(await fixture.Db.InboxMessages.ToListAsync());
        Assert.Empty(await fixture.Db.SyncWatermarks.ToListAsync());
        Assert.Empty(await fixture.Db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DocumentReplacement_RestoresDeletedSnapshotOnFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var old = new CounterpartyDocument
        {
            DocumentId = Guid.NewGuid(),
            DocumentType = "purchasereturn",
            AccountId = fixture.AccountId,
            CounterpartyId = fixture.Counterparty.Id
        };
        fixture.Db.CounterpartyDocuments.Add(old);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var missingCounterpartyId = Guid.NewGuid();
        var replacement = new CounterpartyDocument
        {
            DocumentId = Guid.NewGuid(),
            DocumentType = "purchasereturn",
            AccountId = fixture.AccountId,
            CounterpartyId = missingCounterpartyId
        };
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Documents.ReplaceAsync(
            fixture.AccountId, [fixture.Counterparty.Id, missingCounterpartyId], [replacement], [], CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(old.DocumentId, (await fixture.Db.CounterpartyDocuments.SingleAsync()).DocumentId);
    }

    [Fact]
    public async Task ProductionRepositoryRegistrations_ShareTrackedChangesWithinScopeAndSeparateScopes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = await fixture.Counterparties.FindTrackedAsync(fixture.AccountId, fixture.Counterparty.Id, CancellationToken.None);
        local!.Name = "saved through merge repository";
        await fixture.Merge.SaveProgressAsync(fixture.AccountId, CancellationToken.None);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(local.Name, (await fixture.Db.Counterparties.SingleAsync()).Name);
        Assert.Same(fixture.Merge, fixture.Scope.ServiceProvider.GetRequiredService<IMergeRepository>());
        await using var otherScope = fixture.Provider.CreateAsyncScope();
        Assert.NotSame(fixture.Merge, otherScope.ServiceProvider.GetRequiredService<IMergeRepository>());
        Assert.NotSame(fixture.Db, otherScope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>());
    }

    [Fact]
    public async Task Repositories_FilterForeignIdsAndRejectChangingTenantWithinScope()
    {
        await using var fixture = await Fixture.CreateAsync();
        var otherAccountId = Guid.NewGuid();
        await using (var otherScope = fixture.Provider.CreateAsyncScope())
        {
            var repository = otherScope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();
            Assert.Null(await repository.FindTrackedAsync(otherAccountId, fixture.Counterparty.Id, CancellationToken.None));
            Assert.Empty(await repository.GetSelectionAsync(otherAccountId, [fixture.Counterparty.Id], CancellationToken.None));
            var sync = otherScope.ServiceProvider.GetRequiredService<ISyncRepository>();
            Assert.Null(await sync.FindRunAsync(otherAccountId, fixture.Counterparty.LastSyncRun.MessageId, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => sync.ClearSnapshotAsync(fixture.AccountId, CancellationToken.None));
        }
        Assert.Single(await fixture.Db.Counterparties.ToListAsync());
    }

    private static SyncOutboxMessage Outbox() => new()
    {
        Id = Guid.NewGuid(),
        Topic = "test",
        MessageKey = "test",
        EventType = "test",
        Payload = "{}"
    };

    private sealed class Fixture(SqliteConnection connection, ServiceProvider provider, AsyncServiceScope scope, Counterparty counterparty) : IAsyncDisposable
    {
        public Guid AccountId => Counterparty.AccountId;
        public ServiceProvider Provider { get; } = provider;
        public AsyncServiceScope Scope { get; } = scope;
        public Counterparty Counterparty { get; } = counterparty;
        public CatalogSyncDbContext Db => Scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
        public IMergeRepository Merge => Scope.ServiceProvider.GetRequiredService<IMergeRepository>();
        public ISyncRepository Sync => Scope.ServiceProvider.GetRequiredService<ISyncRepository>();
        public IDocumentSnapshotRepository Documents => Scope.ServiceProvider.GetRequiredService<IDocumentSnapshotRepository>();
        public ICounterpartyRepository Counterparties => Scope.ServiceProvider.GetRequiredService<ICounterpartyRepository>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddDbContext<CatalogSyncDbContext>(options => options.UseSqlite(connection));
            services.AddCatalogRepositories();
            services.AddMergeRepositories();
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
            await db.Database.EnsureCreatedAsync();
            var accountId = Guid.NewGuid();
            var run = new SyncRun { Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), AccountId = accountId, Status = "running" };
            var counterparty = new Counterparty { Id = Guid.NewGuid(), AccountId = accountId, Name = "original", LastSyncRun = run, LastSyncRunId = run.Id };
            db.Counterparties.Add(counterparty);
            await db.SaveChangesAsync();
            return new Fixture(connection, provider, scope, counterparty);
        }

        public async ValueTask DisposeAsync()
        {
            await Scope.DisposeAsync();
            await Provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
