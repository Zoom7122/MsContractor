using System.Text.Json;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

internal static class MergeCounterpartyAttributesParser
{
    public static IReadOnlyList<CounterpartyAttributeDto> Parse(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return [];

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("attributes", out var attributes) ||
                attributes.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return attributes.EnumerateArray()
                .Select(ParseAttribute)
                .Where(attribute => attribute is not null)
                .Cast<CounterpartyAttributeDto>()
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool ValuesEqual(JsonElement? left, JsonElement? right)
    {
        var leftIsNull = left is null || left.Value.ValueKind == JsonValueKind.Null;
        var rightIsNull = right is null || right.Value.ValueKind == JsonValueKind.Null;
        if (leftIsNull || rightIsNull)
            return leftIsNull == rightIsNull;

        return ValuesEqual(left!.Value, right!.Value);
    }

    private static bool ValuesEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
            return false;

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
                var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
                return leftProperties.Count == rightProperties.Count &&
                       leftProperties.All(property => rightProperties.TryGetValue(property.Key, out var value) &&
                                                      ValuesEqual(property.Value, value));
            }
            case JsonValueKind.Array:
            {
                var leftItems = left.EnumerateArray().ToArray();
                var rightItems = right.EnumerateArray().ToArray();
                return leftItems.Length == rightItems.Length &&
                       leftItems.Zip(rightItems).All(pair => ValuesEqual(pair.First, pair.Second));
            }
            case JsonValueKind.String:
                return left.GetString() == right.GetString();
            case JsonValueKind.Number:
                return left.TryGetDecimal(out var leftNumber) && right.TryGetDecimal(out var rightNumber)
                    ? leftNumber == rightNumber
                    : left.GetRawText() == right.GetRawText();
            case JsonValueKind.True:
            case JsonValueKind.False:
                return left.GetBoolean() == right.GetBoolean();
            case JsonValueKind.Null:
                return true;
            default:
                return left.GetRawText() == right.GetRawText();
        }
    }

    private static CounterpartyAttributeDto? ParseAttribute(JsonElement attribute)
    {
        if (attribute.ValueKind != JsonValueKind.Object ||
            !attribute.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(idElement.GetString(), out var id) ||
            id == Guid.Empty)
        {
            return null;
        }

        return new CounterpartyAttributeDto(
            id,
            ReadString(attribute, "name"),
            ReadString(attribute, "type"),
            ReadOptionalValue(attribute, "value"),
            ReadOptionalValue(attribute, "file"),
            ReadOptionalRawJson(attribute, "value"),
            ReadOptionalRawJson(attribute, "file"));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement? ReadOptionalValue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.Clone() : null;

    private static string ReadOptionalRawJson(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetRawText() : "null";
}
