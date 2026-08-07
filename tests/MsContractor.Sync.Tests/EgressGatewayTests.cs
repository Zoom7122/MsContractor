using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using MsContractor.MoySkladEgressService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class EgressGatewayTests
{
    [Fact]
    public async Task UpdateAsync_SendsOnlyExplicitNonNullFields()
    {
        string? body = null;
        HttpMethod? method = null;
        var counterpartyId = Guid.NewGuid();
        var gateway = CreateGateway(request =>
        {
            method = request.Method;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK, $$"""{"id":"{{counterpartyId}}","name":"Updated","archived":false}""");
        });

        await gateway.UpdateAsync(
            Guid.NewGuid(), counterpartyId,
            new InternalCounterpartyUpdateRequest("Updated", null, "+7999", null),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation-id", CancellationToken.None);

        Assert.Equal(HttpMethod.Put, method);
        Assert.Contains("\"name\":\"Updated\"", body);
        using var payload = System.Text.Json.JsonDocument.Parse(body!);
        Assert.Equal("+7999", payload.RootElement.GetProperty("phone").GetString());
        Assert.DoesNotContain("email", body);
        Assert.DoesNotContain("description", body);
    }

    [Fact]
    public async Task ArchiveAsync_AlwaysSendsArchivedTrue()
    {
        string? body = null;
        var counterpartyId = Guid.NewGuid();
        var gateway = CreateGateway(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK, $$"""{"id":"{{counterpartyId}}","name":"Duplicate","archived":true}""");
        });

        await gateway.ArchiveAsync(
            Guid.NewGuid(), counterpartyId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "correlation-id", CancellationToken.None);

        Assert.Equal("{\"archived\":true}", body);
    }

    [Theory]
    [InlineData(false, 1, 0, "filter=archived%3Dfalse")]
    [InlineData(true, 1000, 2000, "filter=archived%3Dtrue")]
    public async Task GetAsync_UsesRequestedFilterLimitOffsetAndBearerToken(
        bool archived,
        int limit,
        int offset,
        string expectedFilter)
    {
        HttpRequestMessage? captured = null;
        var gateway = CreateGateway(
            request =>
            {
                captured = request;
                return Response(HttpStatusCode.OK, """{"meta":{},"rows":[]}""");
            });

        await gateway.GetAsync(
            Guid.NewGuid(),
            archived,
            limit,
            offset,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization?.Parameter);
        var uri = captured.RequestUri!.ToString();
        Assert.Contains("entity/counterparty", uri);
        Assert.Contains(expectedFilter, uri);
        Assert.Contains($"limit={limit}", uri);
        Assert.Contains($"offset={offset}", uri);
        Assert.Equal(
            "application/json; charset=utf-8",
            Assert.Single(captured.Headers.GetValues("Accept")));
        Assert.Contains(captured.Headers.AcceptEncoding, value => value.Value == "gzip");
    }

    [Fact]
    public async Task GetAsync_FormatsIncrementalHalfOpenWindowInMoscowTime()
    {
        HttpRequestMessage? captured = null;
        var gateway = CreateGateway(request =>
        {
            captured = request;
            return Response(HttpStatusCode.OK, """{"meta":{},"rows":[]}""");
        });
        var windowFrom = new DateTimeOffset(2026, 3, 20, 9, 0, 0, 123, TimeSpan.Zero);
        var windowTo = new DateTimeOffset(2026, 3, 20, 10, 0, 0, 456, TimeSpan.Zero);

        await gateway.GetAsync(
            Guid.NewGuid(),
            false,
            1000,
            2000,
            windowFrom,
            windowTo,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-id",
            CancellationToken.None);

        var uri = Uri.UnescapeDataString(captured!.RequestUri!.ToString());
        Assert.Contains(
            "filter=archived=false;updated>=2026-03-20 12:00:00.123;updated<2026-03-20 13:00:00.456",
            uri);
        Assert.Contains("order=updated;id", uri);
        Assert.Contains("offset=2000", uri);
    }

    [Fact]
    public async Task GetAsync_LogsAllStructuredMoySkladErrorMessages()
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(
            _ => Response(
                HttpStatusCode.BadRequest,
                """{"errors":[{"error":"Первая ошибка"},{"error":"Вторая ошибка"}]}"""),
            logger);

        await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(),
            true,
            1000,
            2000,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-id",
            CancellationToken.None));

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("message=Первая ошибка | Вторая ошибка", error.Message);
        Assert.Contains("status=400", error.Message);
        Assert.Contains("archived=True", error.Message);
        Assert.Contains("limit=1000", error.Message);
        Assert.Contains("offset=2000", error.Message);
        Assert.Contains("correlation_id=correlation-id", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.DoesNotContain("Authorization", error.Message);
    }

    [Theory]
    [InlineData("{\"message\":\"unknown shape\"}", "{\"message\":\"unknown shape\"}")]
    [InlineData("not-json error", "not-json error")]
    [InlineData("   ", "MoySklad returned an empty error response.")]
    public async Task GetAsync_LogsFallbackForUnknownErrorBody(string body, string expectedMessage)
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(_ => Response(HttpStatusCode.BadRequest, body), logger);

        await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(),
            false,
            1,
            0,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-id",
            CancellationToken.None));

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains($"message={expectedMessage}"));
    }

    [Fact]
    public async Task GetAsync_TruncatesUnknownErrorBodyTo4096Characters()
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(
            _ => Response(HttpStatusCode.BadRequest, new string('x', 5000)),
            logger);

        await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(),
            false,
            1,
            0,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-id",
            CancellationToken.None));

        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains($"message={new string('x', 4096)},", error.Message);
        Assert.DoesNotContain(new string('x', 4097), error.Message);
    }

    private static MoySkladCounterpartyGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> callback,
        ILogger<MoySkladCounterpartyGateway>? logger = null)
    {
        var client = new HttpClient(new StubHandler(callback))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        return new MoySkladCounterpartyGateway(
            client,
            new FakeTokenClient(),
            new FakeRateLimiter(),
            logger ?? new CaptureLogger<MoySkladCounterpartyGateway>());
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status)
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
        public Task WaitAsync(Guid accountId, Guid? userId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ObserveAsync(
            Guid accountId,
            HttpResponseMessage response,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);
}
