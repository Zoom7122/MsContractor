using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MergeVerifier.Cli;
using MergeVerifier.MoySklad;
using Xunit;
using static MergeVerifier.Tests.Fixtures;

namespace MergeVerifier.Tests;

internal sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request, ++Calls));
    public static HttpResponseMessage Json(JsonNode data) => new(HttpStatusCode.OK)
    { Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json") };
}

public sealed class HttpTests
{
    private static MoySkladClient Client(StubHandler handler, List<TimeSpan>? waits = null) => new(BaseUrl, "тест", "пароль", handler,
        (wait, _) => { waits?.Add(wait); return Task.CompletedTask; }, () => DateTimeOffset.UnixEpoch);

    internal static JsonObject Page(JsonObject[] rows, int size, int offset, int limit = 1000, string? next = null)
    {
        var meta = new JsonObject { ["size"] = size, ["offset"] = offset, ["limit"] = limit };
        if (next is not null) meta["nextHref"] = next;
        return new JsonObject { ["meta"] = meta, ["rows"] = new JsonArray(rows.Select(x => (JsonNode)x.DeepClone()).ToArray()) };
    }

    [Fact]
    public async Task RequestsAreGetOnlyWithUtf8BasicAndGzip()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("тест:пароль")), request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.AcceptEncoding, x => x.Value == "gzip");
            Assert.True(request.Headers.TryGetValues("Accept", out var acceptValues));
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            Assert.Equal("utf-8", request.Headers.Accept.Single().CharSet);
            Assert.NotEmpty(request.Headers.UserAgent);
            return StubHandler.Json(new JsonObject());
        });
        using var client = Client(handler);
        await client.GetAsync("entity/customerorder");
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthErrorsAreNotRetriedOrLeaked(int status)
    {
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("тест:пароль"));
        var handler = new StubHandler((_, _) => new((HttpStatusCode)status)
        { Content = new StringContent("Authorization: Basic " + credentials + " тест:пароль") });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync("entity/customerorder"));
        var message = VerifierApplication.SafeError(exception);
        Assert.Contains("MoySklad authentication failed", message);
        Assert.DoesNotContain("пароль", message); Assert.DoesNotContain("тест", message);
        Assert.DoesNotContain(credentials, message); Assert.DoesNotContain("Authorization", message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task TransientFailuresAreBoundedAndRespectMilliseconds(int status)
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status);
            response.Headers.Add("X-Lognex-Retry-After", "2500");
            response.Headers.Add("X-Lognex-Reset", "3500");
            return response;
        });
        var waits = new List<TimeSpan>();
        using var client = Client(handler, waits);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync("entity/customerorder"));
        Assert.Equal(5, handler.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(3500), waits[0]);
    }

    [Fact]
    public async Task RetryThenSuccessAndRateHeadersAffectPacing()
    {
        var handler = new StubHandler((_, call) =>
        {
            var response = call == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : StubHandler.Json(new JsonObject());
            response.Headers.Add("X-RateLimit-Limit", "10");
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-Lognex-Retry-TimeInterval", "5000");
            response.Headers.RetryAfter = new(System.TimeSpan.FromSeconds(7));
            return response;
        });
        var waits = new List<TimeSpan>();
        using var client = Client(handler, waits);
        await client.GetAsync("entity/customerorder");
        Assert.Equal(2, handler.Calls); Assert.Equal(TimeSpan.FromSeconds(7), waits.Single());
    }

    [Fact]
    public async Task NetworkFailuresRetryWithoutPrintingExceptionSecrets()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("secret-login:secret-password"));
        using var client = Client(handler);
        var exception = await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync("entity/customerorder"));
        Assert.DoesNotContain("secret", exception.Message);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task TimeoutRetriesButCancellationDoesNot()
    {
        var handler = new StubHandler((_, _) => throw new TaskCanceledException("secret"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync("entity/customerorder"));
        Assert.Equal(5, handler.Calls);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("entity/customerorder", cancellation.Token));
        Assert.Equal(5, handler.Calls);
    }

    [Theory]
    [InlineData("https://elsewhere.test/api/remap/1.2/entity/customerorder")]
    [InlineData("http://api.moysklad.ru/api/remap/1.2/entity/customerorder")]
    [InlineData("https://user:password@api.moysklad.ru/api/remap/1.2/entity/customerorder")]
    [InlineData("https://api.moysklad.ru/other/entity/customerorder")]
    [InlineData("entity/../../security/token")]
    public async Task UnsafeLinksCannotReceiveCredentials(string url)
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync(url));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RedirectResponseIsAnError()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.Redirect));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAsync("entity/customerorder"));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AllPagesAreLoadedWithAndWithoutNextHref(bool useNext)
    {
        var path = "entity/customerorder?filter=agent%3Dtest";
        var handler = new StubHandler((request, call) =>
        {
            Assert.Contains("filter=agent", request.RequestUri!.Query);
            return call == 1
                ? StubHandler.Json(Page([new() { ["id"] = "a" }], 2, 0, 1,
                    useNext ? new Uri(BaseUrl, path + "&limit=1&offset=1").AbsoluteUri : null))
                : StubHandler.Json(Page([new() { ["id"] = "b" }], 2, 1, 1));
        });
        using var client = Client(handler);
        var rows = await client.GetAllAsync(path);
        Assert.Equal(2, rows.Count); Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("offset")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    [InlineData("network")]
    public async Task IncompletePaginationNeverSucceeds(string failure)
    {
        var handler = new StubHandler((_, call) =>
        {
            if (call == 1) return StubHandler.Json(Page([new() { ["id"] = "a" }], 2, 0, 1));
            if (failure == "network") throw new HttpRequestException();
            return StubHandler.Json(Page(failure == "empty" ? [] : [new() { ["id"] = failure == "duplicate" ? "a" : "b" }],
                failure == "size" ? 3 : 2, failure == "offset" ? 0 : 1, 1));
        });
        using var client = Client(handler);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAllAsync("entity/customerorder"));
    }

    [Theory]
    [InlineData("entity/customerorder?limit=1&offset=1&filter=different")]
    [InlineData("entity/demand?limit=1&offset=1")]
    [InlineData("entity/customerorder?limit=1&offset=0")]
    public async Task PaginationCannotChangeScopeOrRepeat(string next)
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(Page([new() { ["id"] = "a" }], 2, 0, 1, new Uri(BaseUrl, next).AbsoluteUri)));
        using var client = Client(handler);
        await Assert.ThrowsAsync<VerifierException>(() => client.GetAllAsync("entity/customerorder"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MoreThanOneThousandRowsAreFetched()
    {
        var handler = new StubHandler((_, call) => StubHandler.Json(call == 1
            ? Page(Enumerable.Range(0, 1000).Select(x => new JsonObject { ["id"] = x.ToString() }).ToArray(), 1001, 0)
            : Page([new() { ["id"] = "1000" }], 1001, 1000)));
        using var client = Client(handler);
        Assert.Equal(1001, (await client.GetAllAsync("entity/customerorder")).Count);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task InvalidJsonIsAnErrorWithoutRawResponseInDiagnostic()
    {
        var handler = new StubHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("secret-password-not-json") });
        using var client = Client(handler);
        var exception = await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => client.GetAsync("entity/customerorder"));
        var message = VerifierApplication.SafeError(exception);
        Assert.Contains("Invalid JSON", message);
        Assert.DoesNotContain("secret-password", message);
    }
}
