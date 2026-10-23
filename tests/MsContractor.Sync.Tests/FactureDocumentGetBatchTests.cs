using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.Sync.Tests;

public sealed class FactureDocumentGetBatchTests
{
    private static readonly Guid AccountId = Guid.NewGuid();

    [Theory]
    [InlineData("facturein", 40, 1)]
    [InlineData("facturein", 41, 2)]
    [InlineData("facturein", 81, 3)]
    [InlineData("facturein", 1000, 25)]
    [InlineData("factureout", 40, 1)]
    [InlineData("factureout", 41, 2)]
    [InlineData("factureout", 81, 3)]
    [InlineData("factureout", 1000, 25)]
    public async Task GetAsync_LimitsEachFilterTo40IdsAndCombinesRawDocuments(
        string entityType, int documentCount, int expectedRequests)
    {
        var ids = Ids(documentCount);
        var batches = new List<Guid[]>();
        var limiter = new RecordingRateLimiter();
        var get = CreateGetter(entityType, (request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal($"/api/remap/1.2/entity/{entityType}", request.RequestUri!.AbsolutePath);
            Assert.Contains("limit=1000", request.RequestUri.Query, StringComparison.Ordinal);
            var batch = ReadFilterIds(request);
            Assert.InRange(batch.Length, 1, 40);
            batches.Add(batch);
            return Response(HttpStatusCode.OK, Rows(entityType, batch.Reverse()));
        }, limiter);

        var result = await get(ids, CancellationToken.None);

        Assert.Equal(expectedRequests, batches.Count);
        Assert.Equal(ids, batches.SelectMany(batch => batch));
        Assert.Equal(documentCount, result.Count);
        Assert.All(ids, id => Assert.Equal(RawDocument(entityType, id), result[id]));
        Assert.Equal(expectedRequests, limiter.WaitAccounts.Count);
        Assert.Equal(expectedRequests, limiter.ObservedAccounts.Count);
        Assert.All(limiter.WaitAccounts.Concat(limiter.ObservedAccounts), account => Assert.Equal(AccountId, account));
    }

    [Theory]
    [InlineData("facturein")]
    [InlineData("factureout")]
    public async Task GetAsync_CombinesResponsesWithMissingDocuments(string entityType)
    {
        var ids = Ids(81);
        var returnedIds = new List<Guid>();
        var calls = 0;
        var get = CreateGetter(entityType, (request, _) =>
        {
            calls++;
            var returned = ReadFilterIds(request).Skip(1).ToArray();
            returnedIds.AddRange(returned);
            return Response(HttpStatusCode.OK, Rows(entityType, returned));
        });

        var result = await get(ids, CancellationToken.None);

        Assert.Equal(3, calls);
        Assert.Equal(78, result.Count);
        Assert.Equal(returnedIds.Order(), result.Keys.Order());
        Assert.DoesNotContain(ids[0], result.Keys);
        Assert.DoesNotContain(ids[40], result.Keys);
        Assert.DoesNotContain(ids[80], result.Keys);
    }

    [Theory]
    [InlineData("facturein", "future")]
    [InlineData("facturein", "previous")]
    [InlineData("facturein", "duplicate")]
    [InlineData("factureout", "future")]
    [InlineData("factureout", "previous")]
    [InlineData("factureout", "duplicate")]
    public async Task GetAsync_RejectsDocumentsOutsideCurrentBatchAndDuplicateRows(
        string entityType, string invalidResponse)
    {
        var ids = Ids(81);
        var calls = 0;
        var get = CreateGetter(entityType, (_, _) =>
        {
            calls++;
            // Leave the first response empty so the previous-batch case proves membership validation.
            var returned = invalidResponse switch
            {
                "future" => new[] { ids[40] },
                "duplicate" => new[] { ids[0], ids[0] },
                _ => calls == 1 ? Array.Empty<Guid>() : [ids[0]]
            };
            return Response(HttpStatusCode.OK, Rows(entityType, returned));
        });

        var exception = await Assert.ThrowsAsync<EgressException>(() => get(ids, CancellationToken.None));

        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", exception.Code);
        Assert.Equal(invalidResponse == "previous" ? 2 : 1, calls);
    }

    [Theory]
    [InlineData("facturein", 412, 412)]
    [InlineData("facturein", 429, 429)]
    [InlineData("facturein", 500, 503)]
    [InlineData("factureout", 412, 412)]
    [InlineData("factureout", 429, 429)]
    [InlineData("factureout", 500, 503)]
    public async Task GetAsync_StopsAfterHttpFailureWithoutRetrying(
        string entityType, int upstreamStatus, int expectedStatus)
    {
        var calls = 0;
        var get = CreateGetter(entityType, (request, _) =>
        {
            calls++;
            return calls == 1
                ? Response(HttpStatusCode.OK, Rows(entityType, ReadFilterIds(request)))
                : Response((HttpStatusCode)upstreamStatus, "{\"errors\":[{\"code\":3008,\"error\":\"rejected\"}]}");
        });

        var exception = await Assert.ThrowsAsync<EgressException>(() => get(Ids(81), CancellationToken.None));

        Assert.Equal(2, calls);
        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(upstreamStatus, exception.HttpStatus);
    }

    [Theory]
    [InlineData("facturein")]
    [InlineData("factureout")]
    public async Task GetAsync_HonorsCancellationBeforeTheNextBatch(string entityType)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var get = CreateGetter(entityType, (request, _) =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CancelOnDisposeContent(Rows(entityType, ReadFilterIds(request)), cancellation)
            };
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => get(Ids(81), cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("facturein", "tooMany")]
    [InlineData("facturein", "duplicate")]
    [InlineData("facturein", "emptyId")]
    [InlineData("factureout", "tooMany")]
    [InlineData("factureout", "duplicate")]
    [InlineData("factureout", "emptyId")]
    public async Task GetAsync_ValidatesTheEntireInputBeforeSendingRequests(
        string entityType, string invalidInput)
    {
        var ids = Ids(invalidInput == "tooMany" ? 1001 : 81);
        if (invalidInput == "duplicate") ids[^1] = ids[0];
        if (invalidInput == "emptyId") ids[^1] = Guid.Empty;
        var calls = 0;
        var get = CreateGetter(entityType, (_, _) =>
        {
            calls++;
            return Response(HttpStatusCode.OK, "{\"rows\":[]}");
        });

        await Assert.ThrowsAnyAsync<ArgumentException>(() => get(ids, CancellationToken.None));

        Assert.Equal(0, calls);
    }

    private static Func<IReadOnlyList<Guid>, CancellationToken, Task<IReadOnlyDictionary<Guid, string>>> CreateGetter(
        string entityType,
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond,
        RecordingRateLimiter? limiter = null)
    {
        var client = new HttpClient(new CallbackHandler(respond))
        {
            BaseAddress = new Uri("https://api.test/api/remap/1.2/")
        };
        var tokenClient = new FakeTokenClient();
        limiter ??= new RecordingRateLimiter();
        var responseHandler = new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance);
        if (entityType == "facturein")
        {
            var gateway = new MoySkladFactureInGateway(client, tokenClient, limiter, responseHandler,
                NullLogger<MoySkladFactureInGateway>.Instance);
            return (ids, cancellation) => gateway.GetAsync(AccountId, Guid.Empty, "correlation", ids, cancellation);
        }

        var factureOutGateway = new MoySkladFactureOutGateway(client, tokenClient, limiter, responseHandler,
            NullLogger<MoySkladFactureOutGateway>.Instance);
        return (ids, cancellation) => factureOutGateway.GetAsync(AccountId, "correlation", ids, cancellation);
    }

    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    private static Guid[] ReadFilterIds(HttpRequestMessage request)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        var filter = query.TrimStart('?').Split('&').Single(part => part.StartsWith("filter=", StringComparison.Ordinal))[7..];
        return filter.Split(';').Select(part =>
        {
            Assert.StartsWith("id=", part);
            return Guid.Parse(part[3..]);
        }).ToArray();
    }

    private static string RawDocument(string entityType, Guid id) =>
        $$"""{ "id": "{{id:D}}", "meta": { "type": "{{entityType}}" }, "marker": "raw" }""";

    private static string Rows(string entityType, IEnumerable<Guid> ids) =>
        "{\"rows\":[" + string.Join(',', ids.Select(id => RawDocument(entityType, id))) + "]}";

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class CallbackHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;

        public CallbackHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request, cancellationToken));
    }

    private sealed class CancelOnDisposeContent : StringContent
    {
        private readonly CancellationTokenSource _cancellation;

        public CancelOnDisposeContent(string body, CancellationTokenSource cancellation)
            : base(body, Encoding.UTF8, "application/json")
        {
            _cancellation = cancellation;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) _cancellation.Cancel();
        }
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("test-token");
    }

    private sealed class RecordingRateLimiter : IMoySkladRateLimiter
    {
        public List<Guid> WaitAccounts { get; } = [];
        public List<Guid> ObservedAccounts { get; } = [];

        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken)
        {
            WaitAccounts.Add(accountId);
            return Task.CompletedTask;
        }

        public Task ObserveAsync(Guid accountId, MoySkladRateLimitObservation observation, CancellationToken cancellationToken)
        {
            ObservedAccounts.Add(accountId);
            return Task.CompletedTask;
        }
    }
}
