using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnGatewayTests
{
    [Fact]
    public async Task PurchaseReturnGateway_GetAsync_UsesIdFilterAndKeepsMissingRowsForServiceClassification()
    {
        var requested = new[] { Guid.NewGuid(), Guid.NewGuid() };
        HttpRequestMessage? sentRequest = null;
        var returned = requested[0];
        var gateway = new MoySkladPurchaseReturnGateway(
            Client(request =>
            {
                sentRequest = request;
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    rows = new[] { new { id = returned } }
                }));
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladPurchaseReturnGateway>.Instance);

        var result = await gateway.GetAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", requested, CancellationToken.None);

        Assert.NotNull(sentRequest);
        Assert.Contains("filter=id=", Uri.UnescapeDataString(sentRequest!.RequestUri!.Query));
        Assert.Equal("/api/remap/1.2/entity/purchasereturn", sentRequest.RequestUri.AbsolutePath);
        Assert.Equal([returned], result.Keys);
    }

    [Fact]
    public async Task PositionsGateway_GetPageAsync_UsesPurchasereturnPositionsEndpoint()
    {
        var purchaseReturnId = Guid.NewGuid();
        var positionId = Guid.NewGuid();
        HttpRequestMessage? sentRequest = null;
        var gateway = new MoySkladPurchaseReturnPositionsGateway(
            Client(request =>
            {
                sentRequest = request;
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    meta = new { size = 1, limit = 1000, offset = 0 },
                    rows = new[] { new { id = positionId, quantity = 1 } }
                }));
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladPurchaseReturnPositionsGateway>.Instance);

        var result = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", purchaseReturnId, 1000, 0, CancellationToken.None);

        Assert.Equal(
            $"/api/remap/1.2/entity/purchasereturn/{purchaseReturnId:D}/positions?limit=1000&offset=0",
            sentRequest!.RequestUri!.AbsolutePath + sentRequest.RequestUri.Query);
        Assert.Equal("{\"id\":\"" + positionId + "\",\"quantity\":1}", result.Positions[positionId]);
    }

    [Fact]
    public async Task SupplyGateway_GetAsync_ExtractsAgentAndAllowsMissingSupply()
    {
        var requested = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var returned = requested[0];
        var agentId = Guid.NewGuid();
        var gateway = new MoySkladSupplyGateway(
            Client(_ => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                rows = new[]
                {
                    new
                    {
                        id = returned,
                        agent = new
                        {
                            meta = new
                            {
                                href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{agentId:D}"
                            }
                        }
                    }
                }
            }))),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladSupplyGateway>.Instance);

        var result = await gateway.GetAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", requested, CancellationToken.None);

        Assert.Equal(agentId, result[returned].AgentId);
        Assert.DoesNotContain(requested[1], result.Keys);
    }

    [Fact]
    public async Task PurchaseReturnGateway_BatchMutationsUseExpectedEndpointsAndParseItemResults()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var createdId = Guid.NewGuid();
        var requests = new List<HttpRequestMessage>();
        var gateway = new MoySkladPurchaseReturnGateway(
            Client(request =>
            {
                requests.Add(request);
                if (request.RequestUri!.AbsolutePath.EndsWith("/delete", StringComparison.Ordinal))
                {
                    return Response(HttpStatusCode.OK, $$"""
                    [
                      { "id": "{{firstId:D}}" },
                      { "id": "{{secondId:D}}", "errors": [{ "code": "ITEM_ERROR", "error": "rejected" }] }
                    ]
                    """);
                }

                return Response(HttpStatusCode.OK, $$"""
                [
                  { "id": "{{createdId:D}}" },
                  { "errors": [{ "code": "CREATE_ERROR", "error": "rejected" }] }
                ]
                """);
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladPurchaseReturnGateway>.Instance);

        var deleted = await gateway.DeleteBatchAsync(
            Guid.NewGuid(), "correlation-id", [firstId, secondId], CancellationToken.None);
        var created = await gateway.CreateBatchAsync(
            Guid.NewGuid(),
            "correlation-id",
            [
                new MoySkladPurchaseReturnBatchCreateItem(firstId, "{\"organization\":{}}"),
                new MoySkladPurchaseReturnBatchCreateItem(secondId, "{\"organization\":{}}")
            ],
            CancellationToken.None);

        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.EndsWith("/entity/purchasereturn/delete", requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/entity/purchasereturn/batch", requests[1].RequestUri!.AbsolutePath);
        Assert.True(deleted[0].Succeeded);
        Assert.False(deleted[1].Succeeded);
        Assert.Equal(createdId, created[0].DocumentId);
        Assert.Equal("CREATE_ERROR", created[1].ErrorCode);
    }

    [Fact]
    public async Task PurchaseReturnGateway_ReadsDefaultContractAndCounterpartyAccount()
    {
        var accountId = Guid.NewGuid();
        var counterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var agentAccountId = Guid.NewGuid();
        var requests = new List<HttpRequestMessage>();
        var gateway = new MoySkladPurchaseReturnGateway(
            Client(request =>
            {
                requests.Add(request);
                if (request.RequestUri!.AbsolutePath.EndsWith("/accounts", StringComparison.Ordinal))
                    return Response(HttpStatusCode.OK, $$"""{"rows":[{"id":"{{agentAccountId:D}}","default":true}]}""");
                return Response(HttpStatusCode.OK, $$"""{"rows":[{"id":"{{contractId:D}}","default":true}]}""");
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladPurchaseReturnGateway>.Instance);

        var accounts = await gateway.GetAgentAccountsAsync(accountId, counterpartyId, "correlation-id", CancellationToken.None);
        var contracts = await gateway.GetContractsAsync(accountId, counterpartyId, "correlation-id", CancellationToken.None);

        Assert.Equal(agentAccountId, Assert.Single(accounts).Id);
        Assert.True(accounts[0].IsDefault);
        Assert.Equal(contractId, Assert.Single(contracts).Id);
        Assert.True(contracts[0].IsDefault);
        Assert.EndsWith($"/entity/counterparty/{counterpartyId:D}/accounts", requests[0].RequestUri!.AbsolutePath);
        Assert.EndsWith("/entity/contract", requests[1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PurchaseReturnGateway_RejectsBatchesLargerThan1000()
    {
        var gateway = new MoySkladPurchaseReturnGateway(
            Client(_ => throw new InvalidOperationException("request must not be sent")),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            Handler(),
            NullLogger<MoySkladPurchaseReturnGateway>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.DeleteBatchAsync(
            Guid.NewGuid(), "correlation-id", Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray(), CancellationToken.None));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> callback) =>
        new(new StubHandler(callback))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };

    private static IMoySkladResponseHandler Handler() =>
        new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance);

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
