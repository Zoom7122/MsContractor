using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.Sync.Tests;

public sealed class MoySkladSalesReturnPositionsTests
{
    [Fact]
    public async Task Gateway_GetPageAsync_UsesPositionsEndpointAndParsesRawRows()
    {
        var salesReturnId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        HttpRequestMessage? sentRequest = null;
        var gateway = CreateGateway(request =>
        {
            sentRequest = request;
            return Response(HttpStatusCode.OK, Page(0, [new { id = positionId, quantity = 2 }]));
        });

        var result = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", salesReturnId, 1000, 0, CancellationToken.None);

        Assert.NotNull(sentRequest);
        Assert.Equal(
            $"/api/remap/1.2/entity/salesreturn/{salesReturnId:D}/positions?limit=1000&offset=0",
            sentRequest.RequestUri!.AbsolutePath + sentRequest.RequestUri.Query);
        Assert.Equal("Bearer", sentRequest.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", sentRequest.Headers.Authorization?.Parameter);
        Assert.Equal(1, result.Size);
        Assert.Equal("{\"id\":\"" + positionId + "\",\"quantity\":2}", result.Positions[positionId]);
    }

    [Fact]
    public async Task Service_LoadAsync_LoadsAllPositionPagesBeforeWritingOneSalesReturn()
    {
        var salesReturnId = Guid.NewGuid();
        var firstPositionId = Guid.NewGuid();
        var secondPositionId = Guid.NewGuid();
        var firstPagePositions = Enumerable.Range(0, 999)
            .ToDictionary(_ => Guid.NewGuid(), _ => "{}");
        firstPagePositions[firstPositionId] = "{\"id\":\"" + firstPositionId + "\"}";
        var gateway = new RecordingPositionsGateway(
            new MoySkladSalesReturnPositionsPage(
                1001,
                1000,
                0,
                firstPagePositions),
            new MoySkladSalesReturnPositionsPage(
                1001,
                1000,
                1000,
                new Dictionary<Guid, string> { [secondPositionId] = "{\"id\":\"" + secondPositionId + "\"}" }));
        var repository = new RecordingPositionsRepository();

        await new MoySkladSalesReturnPositionsService(gateway, repository).LoadAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [salesReturnId], CancellationToken.None);

        Assert.Equal([0, 1000], gateway.Offsets);
        var saved = Assert.Single(repository.Items);
        Assert.Equal(salesReturnId, saved.SalesReturnId);
        Assert.Equal(1001, saved.Positions.Count);
        Assert.Contains(firstPositionId, saved.Positions.Keys);
    }

    [Fact]
    public async Task Repository_ReplaceAsync_ReplacesRawPositionsAndKeepsAccountScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new SalesReturnPositionRawDataRepository(dbContext);
        var accountId = Guid.NewGuid();
        var salesReturnId = Guid.NewGuid();
        dbContext.SalesReturnRawData.Add(new SalesReturnRawData
        {
            AccountId = accountId,
            DocumentId = salesReturnId,
            RawJson = "{\"id\":\"document\"}"
        });
        await dbContext.SaveChangesAsync();

        var positionId = Guid.NewGuid();
        await repository.ReplaceAsync(
            accountId,
            salesReturnId,
            new Dictionary<Guid, string> { [positionId] = "{\"version\":1}" },
            CancellationToken.None);
        await repository.ReplaceAsync(
            accountId,
            salesReturnId,
            new Dictionary<Guid, string> { [positionId] = "{\"version\":2}" },
            CancellationToken.None);

        var saved = await dbContext.SalesReturnPositionRawData.SingleAsync();
        Assert.Equal(accountId, saved.AccountId);
        Assert.Equal(salesReturnId, saved.SalesReturnId);
        Assert.Equal(positionId, saved.PositionId);
        Assert.Equal("{\"version\":2}", saved.RawJson);
    }

    [Fact]
    public async Task Repository_GetRequiredAsync_AllowsSalesReturnWithoutRawPositions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new SalesReturnPositionRawDataRepository(dbContext);
        var accountId = Guid.NewGuid();
        var salesReturnId = Guid.NewGuid();
        dbContext.SalesReturnRawData.Add(new SalesReturnRawData
        {
            AccountId = accountId,
            DocumentId = salesReturnId,
            RawJson = "{\"id\":\"document\"}"
        });
        await dbContext.SaveChangesAsync();

        var result = await repository.GetRequiredAsync(
            accountId, [salesReturnId], CancellationToken.None);

        var positions = Assert.Single(result);
        Assert.Equal(salesReturnId, positions.Key);
        Assert.Empty(positions.Value);
    }

    private static MoySkladSalesReturnPositionsGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> callback)
    {
        var client = new HttpClient(new StubHandler(callback))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        return new MoySkladSalesReturnPositionsGateway(
            client,
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladSalesReturnPositionsGateway>.Instance);
    }

    private static string Page(int offset, IReadOnlyList<object> rows) =>
        JsonSerializer.Serialize(new
        {
            meta = new { size = rows.Count + offset, limit = 1000, offset },
            rows
        });

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingPositionsGateway : IMoySkladSalesReturnPositionsGateway
    {
        private readonly Queue<MoySkladSalesReturnPositionsPage> _pages;

        public RecordingPositionsGateway(params MoySkladSalesReturnPositionsPage[] pages) => _pages = new(pages);

        public List<int> Offsets { get; } = [];

        public Task<MoySkladSalesReturnPositionsPage> GetPageAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            Guid salesReturnId,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            Offsets.Add(offset);
            return Task.FromResult(_pages.Dequeue());
        }
    }

    private sealed class RecordingPositionsRepository : ISalesReturnPositionRawDataRepository
    {
        public List<(Guid SalesReturnId, IReadOnlyDictionary<Guid, string> Positions)> Items { get; } = [];

        public Task ReplaceAsync(
            Guid accountId,
            Guid salesReturnId,
            IReadOnlyDictionary<Guid, string> positions,
            CancellationToken cancellationToken)
        {
            Items.Add((salesReturnId, positions));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> salesReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>>(
                new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>());
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("secret-token");
    }

    private sealed class FakeRateLimiter : IMoySkladRateLimiter
    {
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ObserveAsync(
            Guid accountId,
            MoySkladRateLimitObservation observation,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_callback(request));
    }
}
