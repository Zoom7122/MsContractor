using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.MoySklad;

/// <summary>
/// Sequential JSON API 1.2 client. Rate limiting follows script_for_check_merge/MergeVerifier:
/// one request at a time, a minimum spacing, and the X-RateLimit-* / X-Lognex-* headers
/// (Lognex values are milliseconds per the "Обработка ошибок" section of the documentation).
/// </summary>
public sealed class MoySkladApiClient : IMoySkladApi, IDisposable
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan MaxServerDelay = TimeSpan.FromHours(1);

    private readonly HttpClient http;
    private readonly MoySkladCredentials credentials;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> now;
    private readonly Action<string>? trace;
    private readonly TimeSpan minimumSpacing;
    private TimeSpan spacing;
    private DateTimeOffset nextRequest;

    public MoySkladApiClient(Uri baseUrl, MoySkladCredentials credentials, TimeSpan minimumSpacing,
        HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null, Action<string>? trace = null)
    {
        BaseUrl = baseUrl;
        this.credentials = credentials;
        this.minimumSpacing = minimumSpacing;
        spacing = minimumSpacing;
        this.delay = delay ?? Task.Delay;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.trace = trace;
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        })
        {
            BaseAddress = baseUrl,
            Timeout = TimeSpan.FromSeconds(60)
        };
        http.DefaultRequestHeaders.AcceptEncoding.Add(new("gzip"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MsContractor-DocumentRelationsGenerator/1.0");
    }

    public Uri BaseUrl { get; }

    public Task<JsonObject> GetAsync(string pathOrHref, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, pathOrHref, null, repeatable: true, cancellationToken);

    public Task<JsonObject> GetTemplateAsync(string entityType, JsonObject baseDocuments,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"entity/{entityType}/new", baseDocuments, repeatable: true, cancellationToken);

    public Task<JsonObject> CreateAsync(string entityType, JsonObject payload, CancellationToken cancellationToken)
    {
        // syncId makes a repeated POST return the already created object instead of a duplicate
        // (documentation, "Назначение поля syncId").
        var repeatable = payload["syncId"] is JsonValue;
        return SendAsync(HttpMethod.Post, $"entity/{entityType}", payload, repeatable, cancellationToken);
    }

    public async Task DeleteAsync(string pathOrHref, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, pathOrHref, null, repeatable: true, cancellationToken);

    /// <summary>Accepts only API paths on the configured origin, so credentials go nowhere else.</summary>
    public Uri Resolve(string pathOrHref)
    {
        var uri = Uri.TryCreate(pathOrHref, UriKind.Absolute, out var absolute) && absolute.Scheme is "https" or "http"
            ? absolute
            : new Uri(BaseUrl, pathOrHref.TrimStart('/'));
        var allowedPrefixes = new[] { BaseUrl.AbsolutePath + "entity/", BaseUrl.AbsolutePath + "context/" };
        if (uri.Scheme != BaseUrl.Scheme || uri.Authority != BaseUrl.Authority || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || !allowedPrefixes.Any(prefix => uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)))
            throw new GeneratorException($"Refusing to send a request outside the configured API: {uri.AbsolutePath}");
        return uri;
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string pathOrHref, JsonObject? body, bool repeatable,
        CancellationToken cancellationToken)
    {
        var uri = Resolve(pathOrHref);
        var path = uri.AbsolutePath;
        var payload = body?.ToJsonString();
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = nextRequest - now();
                if (wait > TimeSpan.Zero) await delay(wait, cancellationToken);
                nextRequest = now() + spacing;

                HttpResponseMessage response;
                try
                {
                    using var request = new HttpRequestMessage(method, uri);
                    request.Headers.Authorization = credentials.CreateHeader();
                    // MoySklad rejects `Accept: application/json` without the charset (error 1062).
                    request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
                    if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    response = await http.SendAsync(request, cancellationToken);
                }
                catch (Exception ex) when (ex is HttpRequestException ||
                                           ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    trace?.Invoke($"{method} {path} -> transport error (attempt {attempt})");
                    if (!repeatable)
                        throw new MoySkladApiException(method.Method, path, null, null, ex.GetType().Name, outcomeUnknown: true);
                    if (attempt >= MaxAttempts)
                        throw new MoySkladApiException(method.Method, path, null, null, $"transport failure after {attempt} attempts");
                    Backoff(attempt);
                    continue;
                }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    trace?.Invoke($"{method} {path} -> {status}");
                    Observe(response);

                    if (response.IsSuccessStatusCode)
                    {
                        var text = await response.Content.ReadAsStringAsync(cancellationToken);
                        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
                        return JsonNode.Parse(text) as JsonObject
                               ?? throw new MoySkladApiException(method.Method, path, status, null, "response is not a JSON object");
                    }

                    var (code, message) = await ReadApiErrorAsync(response, cancellationToken);
                    // 429 means the request was not processed, so it is safe to repeat even for POST.
                    var retry = status == 429 || repeatable && status is 500 or 502 or 503 or 504;
                    if (retry && attempt < MaxAttempts)
                    {
                        if (status != 429) Backoff(attempt);
                        continue;
                    }

                    var unknown = !repeatable && status >= 500;
                    throw new MoySkladApiException(method.Method, path, status, code, message, unknown);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(int? Code, string? Message)> ReadApiErrorAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        // Only the documented error code/message are kept: the raw body can echo account data.
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (JsonNode.Parse(body) is JsonObject node && node["errors"] is JsonArray { Count: > 0 } errors &&
                errors[0] is JsonObject error)
            {
                int? code = error["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var parsed) ? parsed : null;
                var message = (error["error"] ?? error["error_message"])?.GetValue<string>();
                if (message is not null)
                {
                    message = new string(message.Where(c => !char.IsControl(c)).ToArray());
                    if (message.Length > 400) message = message[..400] + "...";
                }

                return (code, message);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // The HTTP status stays actionable when the error body is not the documented structure.
        }

        return (null, null);
    }

    private void Backoff(int attempt) => Postpone(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

    private void Postpone(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return;
        if (interval > MaxServerDelay)
            throw new GeneratorException("MoySklad asked to wait more than an hour; stopping instead of hammering the API.");
        var target = now() + interval;
        if (target > nextRequest) nextRequest = target;
    }

    private void Observe(HttpResponseMessage response)
    {
        double Header(string name) =>
            response.Headers.TryGetValues(name, out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0
                ? value
                : 0;

        var limit = Header("X-RateLimit-Limit");
        var interval = Header("X-Lognex-Retry-TimeInterval");
        if (limit > 0 && interval > 0)
            spacing = TimeSpan.FromMilliseconds(Math.Max(minimumSpacing.TotalMilliseconds, interval / limit));

        Postpone(TimeSpan.FromMilliseconds(Math.Max(Header("X-Lognex-Retry-After"), Header("X-Lognex-Reset"))));
        if (response.Headers.Contains("X-RateLimit-Remaining") && Header("X-RateLimit-Remaining") == 0 && interval > 0)
            Postpone(TimeSpan.FromMilliseconds(interval));
        if (response.Headers.RetryAfter?.Delta is { } delta) Postpone(delta);
        if ((int)response.StatusCode == 429 && nextRequest <= now()) Postpone(TimeSpan.FromSeconds(1));
    }

    public void Dispose()
    {
        http.Dispose();
        gate.Dispose();
    }
}
