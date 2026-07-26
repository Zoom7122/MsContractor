using System.Text.Json.Serialization;

public sealed class VendorDeactivationRequest
{
    [JsonPropertyName("appUid")]
    public string? AppUid { get; init; }

    [JsonPropertyName("accountName")]
    public string? AccountName { get; init; }

    [JsonPropertyName("cause")]
    public string? Cause { get; init; }
}