using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MergeVerifier.Normalization;

public static class JsonCanonicalizer
{
    public static string Canonicalize(JsonNode? node) => Canonicalize(JsonSerializer.SerializeToElement(node));
    public static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, element);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(value.GetRawText()));
                break;
            default: value.WriteTo(writer); break;
        }
    }

    // Exact decimal representation: no rounding through double, including very large integers.
    private static string NormalizeNumber(string value)
    {
        var parts = value.ToLowerInvariant().Split('e');
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var mantissa = parts[0];
        var negative = mantissa.StartsWith('-');
        mantissa = mantissa.TrimStart('-');
        var dot = mantissa.IndexOf('.');
        if (dot >= 0) exponent = checked(exponent - (mantissa.Length - dot - 1));
        var digits = mantissa.Replace(".", "").TrimStart('0');
        if (digits.Length == 0) return "0";
        var trimmed = digits.TrimEnd('0');
        exponent = checked(exponent + digits.Length - trimmed.Length);
        return (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    public static JsonArray Multiset(IEnumerable<JsonNode?> nodes) => new(nodes
        .OrderBy(Canonicalize, StringComparer.Ordinal).Select(x => x?.DeepClone()).ToArray());
}

public static class SemanticHasher
{
    public static string Hash(JsonElement data) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonCanonicalizer.Canonicalize(data))));
    public static string Hash(JsonNode? data) => Hash(JsonSerializer.SerializeToElement(data));
}
