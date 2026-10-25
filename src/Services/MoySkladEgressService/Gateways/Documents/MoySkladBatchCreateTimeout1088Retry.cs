using System.Net;
using System.Text;
using System.Text.Json;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.ResponseHandling;

namespace MsContractor.MoySkladEgressService.Gateways.Documents;

internal static class MoySkladBatchCreateTimeout1088Retry
{
    private const int MissingResultCode = 108801;

    public static async Task<MoySkladResponseBody> SendAsync<TDocument>(
        IReadOnlyList<TDocument> documents,
        Func<TDocument, Guid> getSyncId,
        Func<IReadOnlyList<TDocument>, CancellationToken, Task<MoySkladResponseBody>> send,
        MoySkladDocumentChangeRetryOptions retryOptions,
        ILogger logger,
        Guid accountId,
        string correlationId,
        string documentType,
        CancellationToken cancellationToken)
    {
        var originalDocuments = documents.ToArray();
        var receivedRows = new Dictionary<Guid, string>(originalDocuments.Length);
        IReadOnlyList<TDocument> pendingDocuments = originalDocuments;
        long retryAttempt = 0;

        while (pendingDocuments.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await send(pendingDocuments, cancellationToken);
            if (!TryReadTimeout1088Rows(response.Body, out var returnedRows))
            {
                if (retryAttempt == 0)
                    return response;

                AddFinalResponseRows(pendingDocuments, getSyncId, response.Body, receivedRows);
                return ComposeResponse(originalDocuments, getSyncId, receivedRows, response.HttpStatus);
            }

            var syncIds = originalDocuments.Select(getSyncId).ToArray();
            if (syncIds.Any(id => id == Guid.Empty) || syncIds.Distinct().Count() != syncIds.Length)
                throw new InvalidOperationException(
                    "A unique non-empty syncId is required to safely retry a MoySklad create batch after error 1088.");

            var pendingSyncIds = pendingDocuments.Select(getSyncId).ToHashSet();
            foreach (var row in returnedRows)
            {
                if (pendingSyncIds.Contains(row.Key))
                    receivedRows[row.Key] = row.Value;
            }

            var missingSyncIds = returnedRows.Keys.ToHashSet();
            var nextPending = pendingDocuments
                .Where(item => !missingSyncIds.Contains(getSyncId(item)))
                .ToArray();

            if (nextPending.Length == 0)
                return ComposeResponse(originalDocuments, getSyncId, receivedRows, response.HttpStatus);

            retryAttempt++;
            var delay = GetRetryDelay(retryOptions, retryAttempt);
            logger.LogWarning(
                "Retrying missing MoySklad document recreation batch items after error 1088: account_id={AccountId}, correlation_id={CorrelationId}, document_type={DocumentType}, retry_attempt={RetryAttempt}, missing_count={MissingCount}, retry_delay_ms={RetryDelayMs}",
                accountId, correlationId, documentType, retryAttempt, nextPending.Length, delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
            pendingDocuments = nextPending;
        }

        return ComposeResponse(originalDocuments, getSyncId, receivedRows, (int)HttpStatusCode.OK);
    }

    public static async Task<MoySkladResponseBody> ReadCreateResponseAsync(
        HttpResponseMessage response,
        MoySkladRequestContext context,
        TimeSpan duration,
        IMoySkladResponseHandler responseHandler,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad response timed out.", duration, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw responseHandler.TransportFailure(
                context, "MoySklad is unavailable.", duration, exception);
        }

        var status = (int)response.StatusCode;
        if (ContainsTimeout1088(body) || response.IsSuccessStatusCode)
            return new MoySkladResponseBody(body, status);

        var contentHeaders = response.Content.Headers.ToArray();
        response.Content.Dispose();
        response.Content = new StringContent(body, Encoding.UTF8);
        foreach (var header in contentHeaders)
            response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);

        return await responseHandler.ReadAsync(response, context, duration, cancellationToken);
    }

    private static bool TryReadTimeout1088Rows(string body, out Dictionary<Guid, string> returnedRows)
    {
        returnedRows = [];
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var response = JsonDocument.Parse(body);
            if (!ContainsTimeout1088(response.RootElement))
                return false;

            if (response.RootElement.ValueKind != JsonValueKind.Array)
                return true;

            var ambiguousSyncIds = new HashSet<Guid>();
            foreach (var row in response.RootElement.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !TryReadGuid(row, "syncId", out var syncId) ||
                    !TryReadGuid(row, "id", out _) ||
                    HasItemErrors(row) ||
                    ambiguousSyncIds.Contains(syncId))
                    continue;

                if (!returnedRows.TryAdd(syncId, row.GetRawText()))
                {
                    returnedRows.Remove(syncId);
                    ambiguousSyncIds.Add(syncId);
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddFinalResponseRows<TDocument>(
        IReadOnlyList<TDocument> pendingDocuments,
        Func<TDocument, Guid> getSyncId,
        string body,
        IDictionary<Guid, string> receivedRows)
    {
        try
        {
            using var response = JsonDocument.Parse(body);
            if (response.RootElement.ValueKind != JsonValueKind.Array)
                return;

            var rows = response.RootElement.EnumerateArray().ToArray();
            var pendingSyncIds = pendingDocuments.Select(getSyncId).ToHashSet();
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                if (row.ValueKind != JsonValueKind.Object)
                    continue;

                if (TryReadGuid(row, "syncId", out var returnedSyncId))
                {
                    if (pendingSyncIds.Contains(returnedSyncId))
                        receivedRows[returnedSyncId] = row.GetRawText();
                    continue;
                }

                if (rows.Length == pendingDocuments.Count && index < pendingDocuments.Count)
                    receivedRows[getSyncId(pendingDocuments[index])] = row.GetRawText();
            }
        }
        catch (JsonException)
        {
            // Missing rows are converted to explicit per-item errors below.
        }
    }

    private static MoySkladResponseBody ComposeResponse<TDocument>(
        IReadOnlyList<TDocument> originalDocuments,
        Func<TDocument, Guid> getSyncId,
        IReadOnlyDictionary<Guid, string> receivedRows,
        int httpStatus)
    {
        var rows = originalDocuments.Select(item =>
        {
            var syncId = getSyncId(item);
            return receivedRows.TryGetValue(syncId, out var row)
                ? row
                : CreateMissingResult(syncId);
        });

        return new MoySkladResponseBody("[" + string.Join(',', rows) + "]", httpStatus);
    }

    private static string CreateMissingResult(Guid syncId)
    {
        var error = "MoySklad did not return a result for this syncId after the batch timeout retry.";
        return JsonSerializer.Serialize(new
        {
            syncId,
            errors = new[] { new { error, code = MissingResultCode } }
        });
    }

    private static TimeSpan GetRetryDelay(
        MoySkladDocumentChangeRetryOptions retryOptions,
        long retryAttempt)
    {
        var exponent = Math.Min(retryAttempt - 1, 1023);
        var exponentialMilliseconds = retryOptions.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, exponent);
        var totalMilliseconds = exponentialMilliseconds + TimeSpan.FromMinutes(6).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(totalMilliseconds, int.MaxValue));
    }

    private static bool ContainsTimeout1088(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        try
        {
            using var document = JsonDocument.Parse(body);
            return ContainsTimeout1088(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsTimeout1088(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryReadCode(element, out var code) && code == 1088)
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (ContainsTimeout1088(property.Value))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsTimeout1088(item))
                    return true;
            }
        }

        return false;
    }

    private static bool TryReadCode(JsonElement error, out int code)
    {
        code = 0;
        if (!error.TryGetProperty("code", out var value))
            return false;

        return value.ValueKind == JsonValueKind.Number
            ? value.TryGetInt32(out code)
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out code);
    }

    private static bool HasItemErrors(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object &&
        row.TryGetProperty("errors", out var errors) &&
        errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;

    private static bool TryReadGuid(JsonElement element, string propertyName, out Guid id)
    {
        id = Guid.Empty;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String &&
               Guid.TryParse(value.GetString(), out id) && id != Guid.Empty;
    }
}
