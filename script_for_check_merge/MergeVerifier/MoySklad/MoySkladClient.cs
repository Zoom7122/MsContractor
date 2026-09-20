using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Configuration;

namespace MergeVerifier.MoySklad;

public sealed class MoySkladClient : IMoySkladClient, IDisposable
{
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue authorization;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly Func<DateTimeOffset> now;
    private DateTimeOffset nextRequest;
    private TimeSpan spacing = TimeSpan.FromMilliseconds(400);
    public Uri BaseUrl { get; }

    public MoySkladClient(Uri baseUrl, string login, string password, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? now = null)
    {
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password) || login.Contains(':'))
            throw new VerifierException("Set MOYSKLAD_LOGIN and MOYSKLAD_PASSWORD; login must not contain a colon.");
        BaseUrl = baseUrl;
        authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{password}")));
        http = new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { BaseAddress = baseUrl, Timeout = TimeSpan.FromSeconds(60) };
        // MoySklad JSON API 1.2 rejects the otherwise common `application/json`
        // value (error 1062); it requires this exact media type with charset.
        // Do not use MediaTypeHeaderValue here: .NET serializes its parameters as
        // `application/json; charset=utf-8`, while MoySklad validates this header
        // literally and accepts only `application/json;charset=utf-8` (error 1062).
        http.DefaultRequestHeaders.AcceptEncoding.Add(new("gzip"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MergeVerifier/1.0");
        this.delay = delay ?? Task.Delay;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public static MoySkladClient FromEnvironment(MergeVerifierOptions options) => new(options.BaseUrl,
        Environment.GetEnvironmentVariable("MOYSKLAD_LOGIN") ?? "",
        Environment.GetEnvironmentVariable("MOYSKLAD_PASSWORD") ?? "");

    public Uri ValidateUri(string path)
    {
        if (!Uri.TryCreate(BaseUrl, path, out var uri) || uri.Scheme != BaseUrl.Scheme ||
            uri.Authority != BaseUrl.Authority || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith(BaseUrl.AbsolutePath + "entity/", StringComparison.Ordinal))
            throw new VerifierException("API supplied an unsafe resource URL; request was not sent.");
        return uri;
    }

    public async Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        var uri = ValidateUri(path);
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var wait = nextRequest - now();
                if (wait > TimeSpan.Zero) await delay(wait, cancellationToken);
                nextRequest = now() + spacing;
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.Authorization = authorization;
                    request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
                    using var response = await http.SendAsync(request, cancellationToken);
                    Observe(response);
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                        throw new VerifierException("MoySklad authentication failed. Check MOYSKLAD_LOGIN and MOYSKLAD_PASSWORD.");
                    if ((int)response.StatusCode is 429 or 502 or 503 or 504)
                    {
                        Backoff(attempt);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var apiError = await ReadSafeApiErrorAsync(response, cancellationToken);
                        var suffix = apiError is null ? "" : $" Details: {apiError}";
                        throw new VerifierException($"MoySklad GET failed (HTTP {(int)response.StatusCode}) for {uri.AbsolutePath}; dataset is incomplete.{suffix}");
                    }
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    return JsonNode.Parse(json) as JsonObject
                        ?? throw new VerifierException("API response must be a JSON object.");
                }
                catch (HttpRequestException) { Backoff(attempt); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Backoff(attempt); }
            }
            throw new VerifierException("MoySklad GET failed after 5 attempts; dataset is incomplete.");
        }
        finally { gate.Release(); }
    }

    private static async Task<string?> ReadSafeApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Keep diagnostics useful without printing the raw response (which could contain
        // arbitrary account data). Only the documented API error code/message are retained.
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(body) as JsonObject;
            if (node?["errors"] is JsonArray errors && errors.FirstOrDefault() is JsonObject error)
            {
                var code = error["code"]?.GetValue<int?>();
                var message = error["error"]?.GetValue<string>() ?? error["message"]?.GetValue<string>();
                if (message is null) return code is null ? null : $"API code {code.Value}.";
                message = new string(message.Where(c => !char.IsControl(c)).ToArray());
                if (message.Length > 400) message = message[..400] + "...";
                return code is null ? message : $"API code {code.Value}: {message}";
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            // The status code itself remains actionable; malformed error bodies are ignored.
        }
        return null;
    }

    private void Backoff(int attempt) => Postpone(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
    private void Postpone(TimeSpan interval)
    {
        // Do not silently cap a server delay and retry earlier than requested.
        if (interval < TimeSpan.Zero || interval > TimeSpan.FromHours(12))
            throw new VerifierException("Invalid or excessive server retry interval.");
        var target = now() + interval;
        if (target > nextRequest) nextRequest = target;
    }

    private void Observe(HttpResponseMessage response)
    {
        double Header(string key) => response.Headers.TryGetValues(key, out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= 0 ? value : 0;
        var limit = Header("X-RateLimit-Limit");
        var interval = Header("X-Lognex-Retry-TimeInterval");
        if (limit > 0 && interval > 0)
        {
            var milliseconds = Math.Max(400, interval / limit);
            if (milliseconds > TimeSpan.FromHours(12).TotalMilliseconds)
                throw new VerifierException("Invalid server rate interval.");
            spacing = TimeSpan.FromMilliseconds(milliseconds);
            Postpone(spacing);
        }
        var retry = Math.Max(Header("X-Lognex-Retry-After"), Header("X-Lognex-Reset"));
        if (retry > TimeSpan.FromHours(12).TotalMilliseconds)
            throw new VerifierException("Excessive server retry interval.");
        Postpone(TimeSpan.FromMilliseconds(retry));
        if (response.Headers.Contains("X-RateLimit-Remaining") && Header("X-RateLimit-Remaining") == 0 && interval > 0)
        {
            if (interval > TimeSpan.FromHours(12).TotalMilliseconds)
                throw new VerifierException("Excessive server rate interval.");
            Postpone(TimeSpan.FromMilliseconds(interval));
        }
        if (response.Headers.RetryAfter?.Delta is { } delta) Postpone(delta);
        if (response.Headers.RetryAfter?.Date is { } date && date > now()) Postpone(date - now());
    }

    public Task<IReadOnlyList<JsonObject>> GetAllAsync(string path, CancellationToken cancellationToken = default) =>
        MoySkladPaginationReader.ReadAsync(this, path, cancellationToken);

    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
