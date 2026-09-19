using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Counterparties;
using MsContractor.MoySkladEgressService.Gateways.Documents;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class EgressGatewayTests
{
    [Theory]
    [InlineData(typeof(MoySkladCounterpartyGateway))]
    [InlineData(typeof(MoySkladDocumentGateway))]
    public void TypedHttpClientFactory_HasUnambiguousPreferredConstructor(Type gatewayType)
    {
        var factory = ActivatorUtilities.CreateFactory(gatewayType, [typeof(HttpClient)]);

        Assert.NotNull(factory);
    }

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
    public async Task ArchiveAsync_SendsBatchWithArchivedTrueAndCounterpartyMeta()
    {
        string? body = null;
        HttpMethod? method = null;
        var counterpartyId = Guid.NewGuid();
        var gateway = CreateGateway(request =>
        {
            method = request.Method;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Response(HttpStatusCode.OK, $$"""{"id":"{{counterpartyId}}","name":"Duplicate","archived":true}""");
        });

        await gateway.ArchiveAsync(
            Guid.NewGuid(), [counterpartyId], Guid.NewGuid(), Guid.NewGuid(),
            "correlation-id", CancellationToken.None);

        Assert.Equal(HttpMethod.Post, method);
        using var payload = System.Text.Json.JsonDocument.Parse(body!);
        var item = Assert.Single(payload.RootElement.EnumerateArray());
        Assert.True(item.GetProperty("archived").GetBoolean());
        Assert.Equal("counterparty", item.GetProperty("meta").GetProperty("type").GetString());
        Assert.Equal(
            "https://api.moysklad.ru/api/remap/1.2/entity/counterparty/metadata",
            item.GetProperty("meta").GetProperty("metadataHref").GetString());
        Assert.EndsWith($"entity/counterparty/{counterpartyId:D}", item.GetProperty("meta").GetProperty("href").GetString());
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
        Assert.Contains("moysklad_error_message=Первая ошибка | Вторая ошибка", error.Message);
        Assert.Contains("http_status=400", error.Message);
        Assert.Contains("endpoint=entity/counterparty?filter=archived%3Dtrue&limit=1000&offset=2000", error.Message);
        Assert.Contains("correlation_id=correlation-id", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
        Assert.DoesNotContain("Authorization", error.Message);
    }

    [Fact]
    public async Task GetAsync_LogsRetryableMoySkladStatusAsError()
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(
            _ => Response(
                HttpStatusCode.ServiceUnavailable,
                """{"errors":[{"code":503,"error":"temporary outage"}]}"""),
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
        Assert.Contains("http_status=503", error.Message);
        Assert.Contains("retryable=True", error.Message);
    }

    [Fact]
    public async Task GetAsync_LogsEscapedMoySkladCyrillicAsReadableText()
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(
            _ => Response(
                HttpStatusCode.PreconditionFailed,
                """{"message":"\u0423\u043A\u0430\u0437\u0430\u043D\u043D\u044B\u0439 \u0434\u043E\u0433\u043E\u0432\u043E\u0440"}"""),
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
        Assert.Contains("response_body={\"message\":\"Указанный договор\"}", error.Message);
        Assert.DoesNotContain("\\u0423", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"message\":\"unknown shape\"}", "{\"message\":\"unknown shape\"}")]
    [InlineData("not-json error", "not-json error")]
    [InlineData("   ", "response_body=")]
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
            entry => entry.Level == LogLevel.Error && entry.Message.Contains(
                expectedMessage == "response_body=" ? expectedMessage : $"response_body={expectedMessage}"));
    }

    [Fact]
    public async Task GetAsync_TruncatesUnknownErrorBodyToConfiguredDiagnosticLimit()
    {
        var logger = new CaptureLogger<MoySkladCounterpartyGateway>();
        var gateway = CreateGateway(
            _ => Response(HttpStatusCode.BadRequest, new string('x', MoySkladResponseHandler.MaximumDiagnosticBodyLength + 100)),
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
        Assert.Contains($"response_body={new string('x', MoySkladResponseHandler.MaximumDiagnosticBodyLength)}", error.Message);
        Assert.DoesNotContain(new string('x', MoySkladResponseHandler.MaximumDiagnosticBodyLength + 1), error.Message);
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
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ObserveAsync(
            Guid accountId,
            MoySkladRateLimitObservation observation,
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
