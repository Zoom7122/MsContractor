namespace MsContractor.MoySkladEgressService.Models.Options;

public sealed class MoySkladDocumentChangeOptions
{
    public required IReadOnlyList<string> DocumentTypes { get; init; }

    public static MoySkladDocumentChangeOptions Parse(string? value, string variableName = "DOCUMENTS_PUT_CHANGE")
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variableName} must contain a comma-separated document type list.");
        var types = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Length == 0 || types.Distinct(StringComparer.Ordinal).Count() != types.Length ||
            types.Any(type => !SupportedMoySkladDocumentTypes.Contains(type)))
        {
            throw new InvalidOperationException($"{variableName} contains duplicate or unsupported document types.");
        }
        return new MoySkladDocumentChangeOptions { DocumentTypes = types };
    }
}
