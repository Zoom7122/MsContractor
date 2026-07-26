using System.Text.Json.Serialization;

namespace MsContractor.VendorService.Contracts;

public sealed class VendorActivationRequest
{
    [JsonPropertyName("appUid")] public string? AppUid { get; init; }
    [JsonPropertyName("accountName")] public string? AccountName { get; init; }
    [JsonPropertyName("cause")] public string? Cause { get; init; }
    [JsonPropertyName("access")] public IReadOnlyList<VendorAccessDto>? Access { get; init; }
    [JsonPropertyName("subscription")] public VendorSubscriptionDto? Subscription { get; init; }
}
