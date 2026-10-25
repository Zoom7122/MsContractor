using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.CatalogSyncService.Controllers;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Models;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class SyncRunHistoryHttpTests
{
    [Fact]
    public async Task Gateway_UsesSessionAccountIgnoresUntrustedQueryAndReturnsStoredCount()
    {
        var account = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var client = new FakeHistoryClient();
        var controller = Gateway(new FakeSessionReader(new GatewaySession(account, Guid.NewGuid())), client);
        controller.Request.QueryString = new QueryString($"?account_id={Guid.NewGuid()}");

        var response = Assert.IsType<OkObjectResult>(await controller.GetErrorsAsync(runId));
        Assert.Equal(account, client.AccountId);
        Assert.Equal(runId, client.RunId);
        Assert.Equal(3, Assert.IsType<SyncRunErrorsResponse>(response.Value).Total);
    }

    [Fact]
    public async Task Gateway_RejectsMissingSessionAndBadPaginationWithoutCallingClient()
    {
        var client = new FakeHistoryClient();
        var unauthorized = Gateway(new FakeSessionReader(null), client);
        Assert.IsType<UnauthorizedObjectResult>(await unauthorized.GetAsync());
        var controller = Gateway(new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())), client);
        Assert.IsType<BadRequestObjectResult>(await controller.GetAsync(0, 25));
        Assert.IsType<BadRequestObjectResult>(await controller.GetAsync(int.MaxValue, 100));
        Assert.IsType<BadRequestObjectResult>(await controller.GetErrorsAsync(Guid.Empty));
        Assert.Equal(Guid.Empty, client.AccountId);
    }

    [Fact]
    public async Task Gateway_DistinguishesMissingRunFromUnavailableService()
    {
        var client = new FakeHistoryClient { Missing = true };
        var controller = Gateway(new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())), client);
        Assert.IsType<NotFoundObjectResult>(await controller.GetErrorsAsync(Guid.NewGuid()));
        client.Unavailable = true;
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.GetAsync()).StatusCode);
    }

    [Fact]
    public async Task InternalApi_RequiresKeyValidatesPaginationAndScopesTheRun()
    {
        var service = new FakeHistoryService();
        var controller = new InternalSyncRunsController(service, Configuration())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var account = Guid.NewGuid();
        Assert.IsType<UnauthorizedObjectResult>(await controller.GetAsync(account));
        controller.Request.Headers[InternalApiHeaders.ApiKey] = "test-only-key";
        Assert.IsType<BadRequestObjectResult>(await controller.GetAsync(account, 1, 101));
        Assert.IsType<BadRequestObjectResult>(await controller.GetErrorsAsync(account, Guid.Empty));
        var result = await controller.GetErrorsAsync(account, Guid.NewGuid(), 2, 25);
        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(account, service.AccountId);
        Assert.Equal(2, service.Page);
        Assert.False(string.IsNullOrWhiteSpace(controller.Response.Headers[InternalApiHeaders.CorrelationId]));
    }

    [Fact]
    public async Task Client_ForwardsInternalContextAndPreservesNotFoundAndCancellation()
    {
        var account = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var correlation = Guid.NewGuid().ToString("D");
        var client = Client(async (request, token) =>
        {
            Assert.Equal($"/internal/accounts/{account:D}/sync-runs/{runId:D}/errors", request.RequestUri!.AbsolutePath);
            Assert.Equal("?page=2&pageSize=25", request.RequestUri.Query);
            Assert.Equal("test-only-key", Assert.Single(request.Headers.GetValues(InternalApiHeaders.ApiKey)));
            Assert.Equal(correlation, Assert.Single(request.Headers.GetValues(InternalApiHeaders.CorrelationId)));
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        Assert.Null(await client.GetErrorsAsync(account, runId, 2, 25, correlation, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetErrorsAsync(account, runId, 2, 25, correlation, canceled.Token));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Client_MapsUnavailableAndMalformedResponses(HttpStatusCode status, string body)
    {
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
        await Assert.ThrowsAsync<CatalogSyncUnavailableException>(() => client.GetAsync(Guid.NewGuid(), 1, 25,
            Guid.NewGuid().ToString("D"), CancellationToken.None));
    }

    [Fact]
    public async Task Client_DeserializesPersistedCountAndRunErrors()
    {
        var runId = Guid.NewGuid();
        var client = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new SyncRunErrorsResponse(Run(runId),
                [new SyncRunErrorState(Guid.NewGuid(), null, "Ошибка запуска", "SYNC_FAILED", DateTimeOffset.UtcNow)], 3, 1, 25))
        }));
        var result = await client.GetErrorsAsync(Guid.NewGuid(), runId, 1, 25, Guid.NewGuid().ToString("D"), CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(runId, result.Run.Id);
        Assert.Equal(3, result.Run.ErrorsCount);
        Assert.Null(Assert.Single(result.Items).CounterpartyId);
    }

    private static SyncRunState Run(Guid id) => new(id, "completed", 2, 3, DateTimeOffset.UtcNow, null,
        DateTimeOffset.UtcNow, "full", null, null, 3, DateTimeOffset.UtcNow);

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["InternalApi:Key"] = "test-only-key" }).Build();

    private static SyncRunsController Gateway(IGatewaySessionReader reader, ISyncRunHistoryClient client) => new(reader, client, Configuration())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static SyncRunHistoryClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        => new(new HttpClient(new Handler(respond)) { BaseAddress = new Uri("http://catalog.test/") }, Configuration());

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) { _respond = respond; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _respond(request, cancellationToken);
    }

    private sealed class FakeSessionReader : IGatewaySessionReader
    {
        private readonly GatewaySession? _session;
        public FakeSessionReader(GatewaySession? session) { _session = session; }
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) => Task.FromResult(_session);
    }

    private sealed class FakeHistoryClient : ISyncRunHistoryClient
    {
        public Guid AccountId { get; private set; }
        public Guid RunId { get; private set; }
        public bool Missing { get; set; }
        public bool Unavailable { get; set; }
        public Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, string correlationId, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            if (Unavailable) throw new CatalogSyncUnavailableException("Unavailable");
            return Task.FromResult(new SyncRunHistoryResponse([], 0, page, pageSize));
        }
        public Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, string correlationId, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            RunId = syncRunId;
            return Task.FromResult(Missing ? null : new SyncRunErrorsResponse(Run(syncRunId), [], 3, page, pageSize));
        }
    }

    private sealed class FakeHistoryService : ISyncRunHistoryService
    {
        public Guid AccountId { get; private set; }
        public int Page { get; private set; }
        public Task<SyncRunHistoryResponse> GetAsync(Guid accountId, int page, int pageSize, CancellationToken cancellationToken)
            => Task.FromResult(new SyncRunHistoryResponse([], 0, page, pageSize));
        public Task<SyncRunErrorsResponse?> GetErrorsAsync(Guid accountId, Guid syncRunId, int page, int pageSize, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            Page = page;
            return Task.FromResult<SyncRunErrorsResponse?>(null);
        }
    }
}
