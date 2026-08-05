namespace MsContractor.Contracts.Internal;

public static class InternalApiHeaders
{
    public const string ApiKey = "X-Internal-Api-Key";
    public const string AccountId = "X-Account-Id";
    public const string CorrelationId = "X-Correlation-Id";
    public const string SyncRunId = "X-Sync-Run-Id";
    public const string UserId = "X-User-Id";
}

public sealed record InternalAccessTokenResponse(string AccessToken);

public sealed record InternalErrorResponse(string Code, string Message);
