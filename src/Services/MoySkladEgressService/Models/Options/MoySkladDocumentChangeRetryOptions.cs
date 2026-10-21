namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed record MoySkladDocumentChangeRetryOptions(
    int MaxRetries,
    TimeSpan InitialRetryDelay,
    bool LogPayloadOnRetry)
{
    public const int DefaultMaxRetries = 2;
    public const int DefaultRetryDelayMs = 5_000;

    public static MoySkladDocumentChangeRetryOptions Parse(
        string? maxRetriesValue,
        string? retryDelayMsValue,
        bool logPayloadOnRetry)
    {
        var maxRetries = ParseNonNegativeInt(maxRetriesValue, DefaultMaxRetries);
        var retryDelayMs = ParseNonNegativeInt(retryDelayMsValue, DefaultRetryDelayMs);
        return new MoySkladDocumentChangeRetryOptions(
            maxRetries,
            TimeSpan.FromMilliseconds(retryDelayMs),
            logPayloadOnRetry);
    }

    public static MoySkladDocumentChangeRetryOptions Disabled { get; } =
        new(0, TimeSpan.Zero, false);

    private static int ParseNonNegativeInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
}
