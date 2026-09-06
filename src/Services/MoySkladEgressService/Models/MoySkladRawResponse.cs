namespace MsContractor.MoySkladEgressService.Models;

public sealed record MoySkladRawResponse(
    string Json,
    int StatusCode,
    string? ContentType,
    IReadOnlyDictionary<string, string> SafeHeaders);
