namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed record PurchaseReturnRecreationOptions(
    int MaxAttempts,
    TimeSpan InitialRetryDelay)
{
    public static PurchaseReturnRecreationOptions Parse(IConfiguration configuration)
    {
        var maxAttempts = ParsePositiveInt(
            configuration["PURCHASERETURN_RECREATE_MAX_ATTEMPTS"], 3);
        var delayMs = ParseNonNegativeInt(
            configuration["PURCHASERETURN_RECREATE_RETRY_DELAY_MS"], 1000);
        return new PurchaseReturnRecreationOptions(maxAttempts, TimeSpan.FromMilliseconds(delayMs));
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static int ParseNonNegativeInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : fallback;
}
