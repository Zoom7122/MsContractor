namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed class MoySkladDocumentDiscoveryOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentDiscoveryOptions Parse(string? value)
    {
        var options = MoySkladDocumentChangeOptions.Parse(value, "DOCUMENTS_DISCOVERY");
        return new MoySkladDocumentDiscoveryOptions { DocumentTypes = options.DocumentTypes };
    }
}
