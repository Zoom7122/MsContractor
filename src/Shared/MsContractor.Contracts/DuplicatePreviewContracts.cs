using System.Text.Json;

namespace MsContractor.Contracts.Duplicates;

public enum DuplicateMatchField
{
    Name,
    Email,
    Phone
}

public sealed record DuplicateCounterpartyDto(
    Guid Id,
    string? Name,
    string? Email,
    string? Phone,
    string? Description,
    bool Archived,
    JsonElement RawJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record DuplicateGroupDto(
    string MatchedBy,
    string MatchValue,
    IReadOnlyList<DuplicateCounterpartyDto> Counterparties);

public static class DuplicateMatchFields
{
    public static bool TryParse(
        IEnumerable<string>? values,
        out IReadOnlyList<DuplicateMatchField> fields)
    {
        var result = new HashSet<DuplicateMatchField>();
        foreach (var value in values ?? [])
        {
            if (!TryParse(value, out var field))
            {
                fields = [];
                return false;
            }

            result.Add(field);
        }

        fields = result.OrderBy(field => field).ToArray();
        return fields.Count > 0;
    }

    public static string ToQueryValue(DuplicateMatchField field) => field switch
    {
        DuplicateMatchField.Name => "name",
        DuplicateMatchField.Email => "email",
        DuplicateMatchField.Phone => "phone",
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static bool TryParse(string? value, out DuplicateMatchField field)
    {
        field = value switch
        {
            "name" => DuplicateMatchField.Name,
            "email" => DuplicateMatchField.Email,
            "phone" => DuplicateMatchField.Phone,
            _ => default
        };
        return value is "name" or "email" or "phone";
    }
}
