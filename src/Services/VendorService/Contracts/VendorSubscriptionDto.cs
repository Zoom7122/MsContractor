using System.Text.Json.Serialization;

namespace MsContractor.VendorService.Contracts;

public sealed class VendorSubscriptionDto
{
    [JsonPropertyName("tariffId")] public Guid? TariffId { get; init; }
    [JsonPropertyName("trial")] public bool Trial { get; init; }
    [JsonPropertyName("tariffName")] public string? TariffName { get; init; }
    [JsonPropertyName("expiryMoment")] public DateTimeOffset? ExpiryMoment { get; init; }
    [JsonPropertyName("notForResale")] public bool NotForResale { get; init; }
    [JsonPropertyName("partner")] public bool Partner { get; init; }
}
