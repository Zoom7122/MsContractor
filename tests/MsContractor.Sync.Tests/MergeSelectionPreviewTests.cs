using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.Gateway.Bff.Models;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.Gateway.Bff.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergeSelectionPreviewTests
{
    [Fact]
    public async Task GetAsync_PreservesRequestedOrderAndAccountScope()
    {
        await using var fixture = await Fixture.CreateAsync();

        var response = await fixture.Service.GetAsync(
            fixture.AccountId,
            new MergeSelectionPreviewRequest([fixture.Second.Id, fixture.First.Id]),
            CancellationToken.None);

        Assert.Equal([fixture.Second.Id, fixture.First.Id], response.Counterparties.Select(item => item.Id));
        Assert.Equal("Second", response.Counterparties[0].Name);
    }

    [Fact]
    public async Task GetAsync_RejectsInvalidSelections()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requests = new[]
        {
            new MergeSelectionPreviewRequest([fixture.First.Id]),
            new MergeSelectionPreviewRequest([fixture.First.Id, fixture.First.Id]),
            new MergeSelectionPreviewRequest([fixture.First.Id, Guid.Empty])
        };

        foreach (var request in requests)
        {
            var exception = await Assert.ThrowsAsync<MergeSelectionPreviewException>(() =>
                fixture.Service.GetAsync(fixture.AccountId, request, CancellationToken.None));
            Assert.Equal(MergeSelectionPreviewError.Invalid, exception.Error);
            Assert.Equal("INVALID_MERGE_PREVIEW_REQUEST", exception.Code);
        }
    }

    [Fact]
    public async Task GetAsync_HidesMissingAndCrossAccountCounterpartiesAsNotFound()
    {
        await using var fixture = await Fixture.CreateAsync();
        var requests = new[]
        {
            new MergeSelectionPreviewRequest([fixture.First.Id, Guid.NewGuid()]),
            new MergeSelectionPreviewRequest([fixture.First.Id, fixture.Foreign.Id])
        };

        foreach (var request in requests)
        {
            var exception = await Assert.ThrowsAsync<MergeSelectionPreviewException>(() =>
                fixture.Service.GetAsync(fixture.AccountId, request, CancellationToken.None));
            Assert.Equal(MergeSelectionPreviewError.NotFound, exception.Error);
            Assert.Equal("COUNTERPARTY_NOT_FOUND", exception.Code);
        }
    }

    [Fact]
    public async Task GetAsync_RejectsLockedSelection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = new MergeJob { Id = Guid.NewGuid(), AccountId = fixture.AccountId,
            MessageId = Guid.NewGuid(), MainCounterpartyId = fixture.First.Id, Payload = "{}" };
        fixture.Db.Add(job);
        fixture.Db.Add(new MergeCounterpartyLock { AccountId = fixture.AccountId,
            CounterpartyId = fixture.Second.Id, MergeJob = job, MergeJobId = job.Id,
            AcquiredAt = DateTimeOffset.UtcNow });
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<MergeSelectionPreviewException>(() => fixture.Service.GetAsync(
            fixture.AccountId, new MergeSelectionPreviewRequest([fixture.First.Id, fixture.Second.Id]),
            CancellationToken.None));

        Assert.Equal(MergeSelectionPreviewError.Busy, exception.Error);
        Assert.Equal("COUNTERPARTY_BUSY", exception.Code);
    }

    [Fact]
    public async Task GatewayController_UsesSessionAccount()
    {
        var accountId = Guid.NewGuid();
        var client = new CapturingClient();
        var controller = GatewayController(new GatewaySession(accountId, Guid.NewGuid()), client);
        var request = new MergeSelectionPreviewRequest([Guid.NewGuid(), Guid.NewGuid()]);

        var result = await controller.CreateAsync(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(accountId, client.AccountId);
        Assert.Same(request, client.Request);
    }

    [Fact]
    public async Task GatewayController_RequiresSession()
    {
        var client = new CapturingClient();
        var controller = GatewayController(null, client);

        var result = await controller.CreateAsync(
            new MergeSelectionPreviewRequest([Guid.NewGuid(), Guid.NewGuid()]),
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(client.AccountId);
    }

    private static MergeSelectionPreviewController GatewayController(
        GatewaySession? session,
        CapturingClient client) => new(
        new FakeSessionReader(session),
        client,
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Session:CookieName"] = "mscontractor.session" }).Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class FakeSessionReader(GatewaySession? session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class CapturingClient : IMergeSelectionPreviewClient
    {
        public Guid? AccountId { get; private set; }
        public MergeSelectionPreviewRequest? Request { get; private set; }

        public Task<MergeSelectionPreviewResponse> GetAsync(
            Guid accountId,
            MergeSelectionPreviewRequest request,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            Request = request;
            return Task.FromResult(new MergeSelectionPreviewResponse([]));
        }
    }

    private sealed class Fixture(SqliteConnection connection, CatalogSyncDbContext db) : IAsyncDisposable
    {
        public Guid AccountId { get; } = Guid.NewGuid();
        public Counterparty First { get; private set; } = null!;
        public Counterparty Second { get; private set; } = null!;
        public Counterparty Foreign { get; private set; } = null!;
        public CatalogSyncDbContext Db => db;
        public MergeSelectionPreviewService Service => new(new CounterpartyRepository(db));

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogSyncDbContext(
                new DbContextOptionsBuilder<CatalogSyncDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var fixture = new Fixture(connection, db);
            var accountRun = Run(fixture.AccountId);
            var foreignAccount = Guid.NewGuid();
            var foreignRun = Run(foreignAccount);
            fixture.First = Counterparty(fixture.AccountId, accountRun, "First");
            fixture.Second = Counterparty(fixture.AccountId, accountRun, "Second");
            fixture.Foreign = Counterparty(foreignAccount, foreignRun, "Foreign");
            db.AddRange(accountRun, foreignRun, fixture.First, fixture.Second, fixture.Foreign);
            await db.SaveChangesAsync();
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }

        private static SyncRun Run(Guid accountId) => new()
        {
            Id = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            AccountId = accountId,
            RequestedByUserId = Guid.NewGuid(),
            Status = "completed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private static Counterparty Counterparty(Guid accountId, SyncRun run, string name) => new()
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Name = name,
            Description = $"{name} description",
            Email = $"{name.ToLowerInvariant()}@example.test",
            Phone = "+70000000000",
            NormalizedName = name.ToLowerInvariant(),
            LastSyncRun = run,
            LastSyncRunId = run.Id,
            RawJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}
