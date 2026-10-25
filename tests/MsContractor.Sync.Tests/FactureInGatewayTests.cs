using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.Sync.Tests;

public sealed class FactureInGatewayTests
{
    [Fact]
    public async Task GetAsync_UsesFactureInIdFilterAndPreservesRawRowJson()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        HttpRequestMessage? sentRequest = null;
        var rawJson = $$"""{ "id": "{{firstId:D}}", "meta": { "type": "facturein" }, "marker": "raw" }""";
        var gateway = new MoySkladFactureInGateway(
            Client(request =>
            {
                sentRequest = request;
                return Response(
                    HttpStatusCode.OK,
                    $$"""{"rows":[{{rawJson}}, {"id":"{{secondId:D}}"}]}""");
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);

        var result = await gateway.GetAsync(
            Guid.NewGuid(),
            Guid.Empty,
            "correlation-id",
            [firstId, secondId],
            CancellationToken.None);

        Assert.NotNull(sentRequest);
        Assert.Equal("/api/remap/1.2/entity/facturein", sentRequest!.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(sentRequest.RequestUri.Query);
        Assert.Contains("limit=1000", query, StringComparison.Ordinal);
        Assert.Contains($"id={firstId:D};id={secondId:D}", query, StringComparison.Ordinal);
        Assert.Equal(rawJson, result[firstId]);
        Assert.Equal($"{{\"id\":\"{secondId:D}\"}}", result[secondId]);
    }

    [Fact]
    public async Task GetAsync_RejectsBatchLargerThan1000()
    {
        var gateway = new MoySkladFactureInGateway(
            Client(_ => Response(HttpStatusCode.OK, "{\"rows\":[]}")),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => gateway.GetAsync(
            Guid.NewGuid(),
            Guid.Empty,
            "correlation-id",
            Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray(),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetExistingBasesAsync_LimitsSupplyIdsTo40PerRequest()
    {
        var requestCounts = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var gateway = new MoySkladFactureInGateway(
            Client(request =>
            {
                var entityType = request.RequestUri!.AbsolutePath.Split('/').Last();
                var query = Uri.UnescapeDataString(request.RequestUri.Query);
                var filter = query.TrimStart('?').Split('&')
                    .Single(part => part.StartsWith("filter=", StringComparison.Ordinal))[7..];
                var count = filter.Split(';').Length;
                if (!requestCounts.TryGetValue(entityType, out var counts))
                {
                    counts = [];
                    requestCounts.Add(entityType, counts);
                }
                counts.Add(count);
                return Response(HttpStatusCode.OK, "{\"rows\":[]}");
            }),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);
        var references = Enumerable.Range(0, 85)
            .Select(_ => new FactureInBaseReference("supply", Guid.NewGuid()))
            .Concat(Enumerable.Range(0, 85)
                .Select(_ => new FactureInBaseReference("paymentout", Guid.NewGuid())))
            .ToArray();

        await gateway.GetExistingBasesAsync(
            Guid.NewGuid(), "correlation-id", references, CancellationToken.None);

        Assert.Equal(new[] { 40, 40, 5 }, requestCounts["supply"]);
        Assert.Equal(new[] { 85 }, requestCounts["paymentout"]);
    }

    [Fact]
    public async Task GetAsync_RejectsUnexpectedDocumentAndDuplicateRows()
    {
        var requestedId = Guid.NewGuid();
        var unexpectedId = Guid.NewGuid();
        var gateway = Gateway(body: $$"""{"rows":[{"id":"{{unexpectedId:D}}"}]}""");

        var unexpected = await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", [requestedId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", unexpected.Code);

        gateway = Gateway(body: $$"""{"rows":[{"id":"{{requestedId:D}}"},{"id":"{{requestedId:D}}"}]}""");
        var duplicate = await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", [requestedId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", duplicate.Code);
    }

    [Fact]
    public async Task GetAsync_MapsHttpFailureThroughResponseHandler()
    {
        var gateway = new MoySkladFactureInGateway(
            Client(_ => Response(HttpStatusCode.InternalServerError, "{}")),
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.Empty, "correlation-id", [Guid.NewGuid()], CancellationToken.None));
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public async Task DeleteBatchAsync_UsesFactureInDeleteAndReturnsPerItemFailures()
    {
        var successfulId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        HttpRequestMessage? sent = null;
        var gateway = new MoySkladFactureInGateway(
            Client(request =>
            {
                sent = request;
                return Response(HttpStatusCode.OK, $$"""[{"id":"{{successfulId:D}}"},{"id":"{{failedId:D}}","errors":[{"code":"3008","error":"rejected"}]}]""");
            }), new FakeTokenClient(), new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);

        var result = await gateway.DeleteBatchAsync(Guid.NewGuid(), "correlation", [successfulId, failedId], CancellationToken.None);

        Assert.Equal("/api/remap/1.2/entity/facturein/delete", sent!.RequestUri!.AbsolutePath);
        Assert.True(result[0].Succeeded);
        Assert.False(result[1].Succeeded);
        Assert.Equal("3008", result[1].ErrorCode);
    }

    [Fact]
    public async Task CreateBatchAsync_ParsesEachBatchItemEvenWhenHttpIsSuccessful()
    {
        var sourceId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var failedSourceId = Guid.NewGuid();
        var failedSyncId = Guid.NewGuid();
        var gateway = new MoySkladFactureInGateway(
            Client(_ => Response(HttpStatusCode.OK,
                $$$"""[{"id":"{{{newId:D}}}","syncId":"{{{syncId:D}}}","meta":{"type":"facturein"}},{"syncId":"{{{failedSyncId:D}}}","errors":[{"code":"3008","error":"rejected"}]}]""")),
            new FakeTokenClient(), new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladFactureInGateway>.Instance);

        var result = await gateway.CreateBatchAsync(Guid.NewGuid(), "correlation",
            [new MoySkladFactureInBatchCreateItem(sourceId, syncId, $$"""{"syncId":"{{syncId:D}}"}"""),
                new MoySkladFactureInBatchCreateItem(failedSourceId, failedSyncId, $$"""{"syncId":"{{failedSyncId:D}}"}""")],
            CancellationToken.None);

        var created = result[0];
        Assert.Equal(newId, created.DocumentId);
        Assert.Equal(syncId, created.ReturnedSyncId);
        Assert.Equal("facturein", created.DocumentType);
        Assert.Equal("3008", result[1].ErrorCode);
    }

    private static MoySkladFactureInGateway Gateway(string body) => new(
        Client(_ => Response(HttpStatusCode.OK, body)),
        new FakeTokenClient(),
        new FakeRateLimiter(),
        new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
        NullLogger<MoySkladFactureInGateway>.Instance);

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> callback) =>
        new(new CallbackHandler(callback))
        {
            BaseAddress = new Uri("https://api.test/api/remap/1.2/")
        };

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("access-token");
    }

    private sealed class FakeRateLimiter : IMoySkladRateLimiter
    {
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ObserveAsync(
            Guid accountId,
            MoySkladRateLimitObservation observation,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
