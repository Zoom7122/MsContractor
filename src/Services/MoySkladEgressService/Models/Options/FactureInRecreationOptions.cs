namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed record FactureInRecreationOptions(
    int MaxAttempts,
    TimeSpan InitialRetryDelay)
{
    public static FactureInRecreationOptions Parse(IConfiguration configuration)
    {
        var maxAttempts = ParsePositiveInt(configuration["FACTUREIN_RECREATE_MAX_ATTEMPTS"], 3);
        var delayMs = ParseNonNegativeInt(configuration["FACTUREIN_RECREATE_RETRY_DELAY_MS"], 1000);
        return new FactureInRecreationOptions(maxAttempts, TimeSpan.FromMilliseconds(delayMs));
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int ParseNonNegativeInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
}
