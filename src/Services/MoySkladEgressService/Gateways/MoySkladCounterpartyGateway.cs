using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Clients;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using MsContractor.Contracts.Internal;

namespace MsContractor.MoySkladEgressService.Gateways;

public interface IMoySkladCounterpartyGateway
{
    Task<MoySkladRawResponse> GetAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladRawResponse> UpdateAsync(
        Guid accountId, Guid counterpartyId, InternalCounterpartyUpdateRequest update,
        Guid mergeJobId, Guid operationId, Guid userId, string correlationId,
        CancellationToken cancellationToken);

    Task<MoySkladRawResponse> ArchiveAsync(
        Guid accountId, IReadOnlyList<Guid> counterpartyIds, Guid mergeJobId,
        Guid userId, string correlationId, CancellationToken cancellationToken);
}

public sealed class MoySkladCounterpartyGateway : IMoySkladCounterpartyGateway
{
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);
    private readonly HttpClient _httpClient;
    private readonly IVendorTokenClient _tokenClient;
    private readonly IMoySkladRateLimiter _rateLimiter;
    private readonly ILogger<MoySkladCounterpartyGateway> _logger;
    private readonly IMoySkladResponseHandler _responseHandler;

    [ActivatorUtilitiesConstructor]
    public MoySkladCounterpartyGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladCounterpartyGateway> logger,
        IMoySkladResponseHandler responseHandler)
    {
        _httpClient = httpClient;
        _tokenClient = tokenClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
        _responseHandler = responseHandler;
    }

    public MoySkladCounterpartyGateway(
        HttpClient httpClient,
        IVendorTokenClient tokenClient,
        IMoySkladRateLimiter rateLimiter,
        ILogger<MoySkladCounterpartyGateway> logger)
        : this(
            httpClient,
            tokenClient,
            rateLimiter,
            logger,
            new MoySkladResponseHandler(
                new ForwardingLogger<MoySkladCounterpartyGateway, MoySkladResponseHandler>(logger)))
    {
    }

    public async Task<MoySkladRawResponse> GetAsync(
        Guid accountId,
        bool archived,
        int limit,
        int offset,
        DateTimeOffset? windowFrom,
        DateTimeOffset? windowTo,
        Guid syncRunId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, userId, cancellationToken);

        var filter = $"archived={archived.ToString().ToLowerInvariant()}";
        if (windowFrom is not null && windowTo is not null)
        {
            filter += $";updated>={FormatMoySkladTimestamp(windowFrom.Value)}" +
                      $";updated<{FormatMoySkladTimestamp(windowTo.Value)}";
        }

        var query = $"entity/counterparty?filter={Uri.EscapeDataString(filter)}" +
                    $"&limit={limit}&offset={offset}";
        if (windowFrom is not null)
            query += $"&order={Uri.EscapeDataString("updated;id")}";
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));

        // Never log the Authorization header: it contains the account's access token.
        var requestUri = request.RequestUri is { IsAbsoluteUri: true } uri
            ? uri
            : new Uri(_httpClient.BaseAddress!, request.RequestUri!);
        _logger.LogInformation(
            "Sending MoySklad counterparties request: account_id={AccountId}, sync_run_id={SyncRunId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, archived={Archived}, limit={Limit}, offset={Offset}, method={Method}, url={Url}, accept={Accept}, accept_encoding={AcceptEncoding}",
            accountId,
            syncRunId,
            userId,
            correlationId,
            archived,
            limit,
            offset,
            request.Method,
            requestUri,
            request.Headers.Accept.ToString(),
            request.Headers.AcceptEncoding.ToString());

        var context = new MoySkladRequestContext(
            accountId, correlationId, null, null, "GET", query, "counterparty", null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response,
                context,
                stopwatch.Elapsed,
                cancellationToken);
            var json = responseBody.Body;
            _logger.LogInformation(
                "MoySklad counterparties response received: account_id={AccountId}, sync_run_id={SyncRunId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}, archived={Archived}, limit={Limit}, offset={Offset}, status={StatusCode}, content_type={ContentType}, headers={Headers}, duration_ms={DurationMs}",
                accountId,
                syncRunId,
                userId,
                correlationId,
                archived,
                limit,
                offset,
                responseBody.HttpStatus,
                response.Content.Headers.ContentType?.MediaType,
                SafeHeaders(response),
                stopwatch.Elapsed.TotalMilliseconds);

            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(
                    context,
                    responseBody.HttpStatus,
                    "MoySklad returned an empty response.",
                    json,
                    stopwatch.Elapsed);

            return new MoySkladRawResponse(
                json,
                responseBody.HttpStatus,
                response.Content.Headers.ContentType?.MediaType,
                SafeHeaders(response));
        }
    }

    public Task<MoySkladRawResponse> UpdateAsync(
        Guid accountId,
        Guid counterpartyId,
        InternalCounterpartyUpdateRequest update,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?> { ["name"] = update.Name };
        if (update.Email is not null) payload["email"] = update.Email;
        if (update.Phone is not null) payload["phone"] = update.Phone;
        if (update.Description is not null) payload["description"] = update.Description;
        return PutAsync(accountId, counterpartyId, payload, mergeJobId, operationId, userId, correlationId, cancellationToken);
    }

    public Task<MoySkladRawResponse> ArchiveAsync(
        Guid accountId,
        IReadOnlyList<Guid> counterpartyIds,
        Guid mergeJobId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var payload = counterpartyIds.Select(counterpartyId => new
        {
            meta = new
            {
                href = new Uri(_httpClient.BaseAddress!, $"entity/counterparty/{counterpartyId:D}").ToString(),
                metadataHref = new Uri(_httpClient.BaseAddress!, "entity/counterparty/metadata").ToString(),
                type = "counterparty",
                mediaType = "application/json"
            },
            archived = true
        }).ToArray();

        return PostBatchAsync(accountId, payload, mergeJobId, userId, correlationId, cancellationToken);
    }

    private async Task<MoySkladRawResponse> PutAsync(
        Guid accountId,
        Guid counterpartyId,
        IReadOnlyDictionary<string, object?> payload,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        await _rateLimiter.WaitAsync(accountId, userId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"entity/counterparty/{counterpartyId:D}")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");
        _logger.LogInformation(
            "Sending MoySklad counterparty mutation: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, counterparty_id={CounterpartyId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}",
            accountId, mergeJobId, operationId, counterpartyId, userId, correlationId);

        var context = new MoySkladRequestContext(
            accountId, correlationId, mergeJobId, operationId, "PUT",
            $"entity/counterparty/{counterpartyId:D}", "counterparty", counterpartyId);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            var json = responseBody.Body;
            _logger.LogInformation(
                "MoySklad counterparty mutation completed: account_id={AccountId}, merge_job_id={MergeJobId}, operation_id={OperationId}, counterparty_id={CounterpartyId}, status={StatusCode}, correlation_id={CorrelationId}",
                accountId, mergeJobId, operationId, counterpartyId, responseBody.HttpStatus, correlationId);
            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty response.", json,
                    stopwatch.Elapsed);
            return new MoySkladRawResponse(
                json, responseBody.HttpStatus,
                response.Content.Headers.ContentType?.MediaType, SafeHeaders(response));
        }
    }

    private async Task<MoySkladRawResponse> PostBatchAsync<T>(
        Guid accountId,
        T payload,
        Guid mergeJobId,
        Guid userId,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var accessToken = await _tokenClient.GetAccessTokenAsync(accountId, cancellationToken);
        //Заглушка сделать лимитер
        await _rateLimiter.WaitAsync(accountId, userId, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, "entity/counterparty")
        {
            Content = JsonContent.Create(payload)
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=utf-8");

        _logger.LogInformation(
            "Sending MoySklad counterparty batch archive: account_id={AccountId}, merge_job_id={MergeJobId}, requested_by_user_id={UserId}, correlation_id={CorrelationId}",
            accountId, mergeJobId, userId, correlationId);

        var context = new MoySkladRequestContext(
            accountId, correlationId, mergeJobId, null, "POST", "entity/counterparty",
            "counterparty", null);
        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad request timed out.", stopwatch.Elapsed, exception);
        }
        catch (HttpRequestException exception)
        {
            throw _responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", stopwatch.Elapsed, exception);
        }

        using (response)
        {
            await _rateLimiter.ObserveAsync(accountId, response, cancellationToken);
            var responseBody = await _responseHandler.ReadAsync(
                response, context, stopwatch.Elapsed, cancellationToken);
            _logger.LogInformation(
                "MoySklad counterparty batch archive completed: account_id={AccountId}, merge_job_id={MergeJobId}, status={StatusCode}, correlation_id={CorrelationId}",
                accountId, mergeJobId, responseBody.HttpStatus, correlationId);
            var json = responseBody.Body;
            if (string.IsNullOrWhiteSpace(json))
                throw _responseHandler.ValidationFailure(
                    context, responseBody.HttpStatus, "MoySklad returned an empty response.", json,
                    stopwatch.Elapsed);
            return new MoySkladRawResponse(json, responseBody.HttpStatus,
                response.Content.Headers.ContentType?.MediaType, SafeHeaders(response));
        }
    }

    private static string FormatMoySkladTimestamp(DateTimeOffset value) =>
        value.ToOffset(MoscowOffset).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> SafeHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[]
                 {
                     "X-RateLimit-Limit",
                     "X-RateLimit-Remaining",
                     "X-Lognex-Retry-After",
                     "X-Lognex-Reset"
                 })
        {
            if (response.Headers.TryGetValues(name, out var values))
                result[name] = values.FirstOrDefault() ?? string.Empty;
        }

        return result;
    }
}
