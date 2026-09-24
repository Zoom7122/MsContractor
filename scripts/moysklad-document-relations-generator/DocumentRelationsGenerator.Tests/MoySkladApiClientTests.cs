using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DocumentRelationsGenerator.MoySklad;

namespace DocumentRelationsGenerator.Tests;

public sealed class MoySkladApiClientTests
{
    private const string Password = "S3cr3t-pass";
    private static readonly Uri Base = new("https://api.moysklad.ru/api/remap/1.2/");

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();
        public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

        public Handler Then(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? headers = null)
        {
            responses.Enqueue(_ =>
            {
                var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                headers?.Invoke(response);
                return response;
            });
            return this;
        }

        public Handler ThenThrow()
        {
            responses.Enqueue(_ => throw new HttpRequestException("connection reset"));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue()(request);
        }
    }

    private static (MoySkladApiClient Client, List<TimeSpan> Delays) Client(Handler handler)
    {
        var delays = new List<TimeSpan>();
        var clock = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var client = new MoySkladApiClient(Base, MoySkladCredentials.FromLogin("admin@test", Password), TimeSpan.FromMilliseconds(400), handler,
            (delay, _) =>
            {
                delays.Add(delay);
                clock += delay;
                return Task.CompletedTask;
            },
            () => clock);
        return (client, delays);
    }

    [Fact]
    public async Task Requests_CarryBasicAuthAndTheExactAcceptHeader()
    {
        var handler = new Handler().Then(HttpStatusCode.OK, "{\"rows\":[]}");
        var (client, _) = Client(handler);

        await client.GetAsync("entity/organization?limit=100", CancellationToken.None);

        var request = handler.Requests.Single().Request;
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin@test:{Password}")), request.Headers.Authorization.Parameter);
        Assert.Equal("/api/remap/1.2/entity/organization", request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task AcceptHeaderOnTheWire_HasNoSpaceBeforeCharset()
    {
        // Reading headers back through HttpHeaders re-formats them, so the check is done on raw TCP bytes.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var buffer = new byte[16384];
            var total = 0;
            string text;
            do
            {
                total += await stream.ReadAsync(buffer.AsMemory(total));
                text = Encoding.ASCII.GetString(buffer, 0, total);
            } while (!text.Contains("\r\n\r\n", StringComparison.Ordinal));
            const string response = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
            return text;
        });
        try
        {
            using var client = new MoySkladApiClient(new Uri($"http://127.0.0.1:{port}/api/remap/1.2/"),
                MoySkladCredentials.FromLogin("admin@test", Password), TimeSpan.FromMilliseconds(100));
            await client.GetAsync("entity/customerorder", CancellationToken.None);
            var request = await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Accept: application/json;charset=utf-8\r\n", request, StringComparison.Ordinal);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Status429_WaitsForLognexRetryAfterAndRepeats()
    {
        var handler = new Handler()
            .Then((HttpStatusCode)429, "{\"errors\":[{\"error\":\"limit\",\"code\":1049}]}",
                response => response.Headers.Add("X-Lognex-Retry-After", "3000"))
            .Then(HttpStatusCode.OK);
        var (client, delays) = Client(handler);

        await client.CreateAsync("demand", new JsonObject { ["name"] = "no syncId" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count); // 429 = not processed, safe to repeat even without syncId
        Assert.Contains(delays, delay => delay >= TimeSpan.FromMilliseconds(3000));
    }

    [Fact]
    public async Task PostWithoutSyncId_IsNotRepeatedAfterServerError()
    {
        var handler = new Handler().Then(HttpStatusCode.ServiceUnavailable).Then(HttpStatusCode.OK);
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<MoySkladApiException>(() =>
            client.CreateAsync("product", new JsonObject { ["name"] = "x" }, CancellationToken.None));

        Assert.True(error.OutcomeUnknown);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PostWithoutSyncId_IsNotRepeatedAfterTransportFailure()
    {
        var handler = new Handler().ThenThrow().Then(HttpStatusCode.OK);
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<MoySkladApiException>(() =>
            client.CreateAsync("contract", new JsonObject(), CancellationToken.None));

        Assert.True(error.OutcomeUnknown);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PostWithSyncId_IsRepeatedWithTheSameBody()
    {
        var handler = new Handler().ThenThrow().Then(HttpStatusCode.OK, "{\"id\":\"1\"}");
        var (client, _) = Client(handler);

        await client.CreateAsync("demand", new JsonObject { ["syncId"] = "b3b0c1a2-0000-5000-8000-000000000001" }, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
    }

    [Fact]
    public async Task Errors_ExposeCodeAndMessageButNeverCredentials()
    {
        var handler = new Handler().Then(HttpStatusCode.PreconditionFailed,
            "{\"errors\":[{\"error\":\"Ошибка сохранения: поле 'demand' не может быть изменено\",\"code\":3000}]}");
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<MoySkladApiException>(() =>
            client.GetTemplateAsync("salesreturn", new JsonObject(), CancellationToken.None));

        Assert.Equal(412, error.StatusCode);
        Assert.Equal(3000, error.ErrorCode);
        Assert.Contains("demand", error.ErrorMessage);
        Assert.DoesNotContain(Password, error.ToString());
        Assert.DoesNotContain("Basic", error.ToString());
    }

    [Fact]
    public async Task AuthenticationFailure_IsNotRetried()
    {
        var handler = new Handler().Then(HttpStatusCode.Unauthorized, "");
        var (client, _) = Client(handler);

        var error = await Assert.ThrowsAsync<MoySkladApiException>(() => client.GetAsync("entity/store", CancellationToken.None));
        Assert.True(error.IsAuthenticationFailure);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Resolve_RefusesOtherHostsAndNonApiPaths()
    {
        var (client, _) = Client(new Handler());
        Assert.Throws<GeneratorException>(() => client.Resolve("https://evil.example.com/api/remap/1.2/entity/demand/1"));
        Assert.Throws<GeneratorException>(() => client.Resolve("https://api.moysklad.ru/api/remap/1.1/entity/demand/1"));
        Assert.Throws<GeneratorException>(() => client.Resolve("security/token"));
        Assert.Equal("/api/remap/1.2/entity/demand/1", client.Resolve("https://api.moysklad.ru/api/remap/1.2/entity/demand/1?expand=x").AbsolutePath);
    }

    [Fact]
    public void Credentials_DoNotPrintTheSecret()
    {
        Assert.DoesNotContain(Password, MoySkladCredentials.FromLogin("admin@test", Password).ToString());
        Assert.DoesNotContain("tok-123", MoySkladCredentials.FromToken("tok-123").ToString());
    }
}
