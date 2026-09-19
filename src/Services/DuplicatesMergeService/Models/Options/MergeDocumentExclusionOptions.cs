namespace MsContractor.DuplicatesMergeService.Models.Options;

public sealed class MergeDocumentExclusionOptions
{
    public required IReadOnlySet<string> DocumentTypes { get; init; }

    public static MergeDocumentExclusionOptions Parse(
        string? value,
        string variableName = "DOCUMENTS_MERGE_EXCLUDE")
    {
        if (string.IsNullOrWhiteSpace(value))
            return new MergeDocumentExclusionOptions { DocumentTypes = new HashSet<string>(StringComparer.Ordinal) };

        var types = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (types.Any(string.IsNullOrWhiteSpace) ||
            types.Distinct(StringComparer.Ordinal).Count() != types.Length)
        {
            throw new InvalidOperationException(
                $"{variableName} contains duplicate document types.");
        }

        return new MergeDocumentExclusionOptions
        {
            DocumentTypes = types.ToHashSet(StringComparer.Ordinal)
        };
    }
}
