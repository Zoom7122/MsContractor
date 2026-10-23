namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed record PurchaseReturnSupplyRetryOptions(int MaxRetries)
{
    public const int DefaultMaxRetries = 2;

    public static PurchaseReturnSupplyRetryOptions Parse(IConfiguration configuration)
    {
        var value = configuration["PURCHASERETURN_SUPPLY_MAX_RETRIES"];
        var maxRetries = int.TryParse(value, out var parsed) && parsed >= 0
            ? parsed
            : DefaultMaxRetries;
        return new PurchaseReturnSupplyRetryOptions(maxRetries);
    }
}
