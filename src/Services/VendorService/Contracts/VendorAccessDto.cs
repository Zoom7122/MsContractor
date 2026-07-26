using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsContractor.VendorService.Contracts;

public sealed class VendorAccessDto
{
    [JsonPropertyName("resource")] public string? Resource { get; init; }
    [JsonPropertyName("scope")] public IReadOnlyList<string>? Scope { get; init; }
    [JsonPropertyName("permissions")] public JsonElement? Permissions { get; init; }
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
}
