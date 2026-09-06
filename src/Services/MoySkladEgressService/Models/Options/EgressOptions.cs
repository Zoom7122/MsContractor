namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed class EgressOptions
{
    public Uri JsonApiBaseUrl { get; init; } =
        new("https://api.moysklad.ru/api/remap/1.2/");

    public Uri VendorServiceBaseUrl { get; init; } =
        new("http://localhost:5011/");

    public string InternalApiKey { get; init; } = string.Empty;
}
