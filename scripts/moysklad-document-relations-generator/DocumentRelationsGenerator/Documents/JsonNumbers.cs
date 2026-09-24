using System.Globalization;
using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.Documents;

public static class JsonNumbers
{
    /// <summary>
    /// Reads any numeric JSON value. A JsonValue built in memory from long/int does not convert through
    /// TryGetValue&lt;decimal&gt;, one parsed from a response does; the text form works for both.
    /// </summary>
    public static decimal Read(JsonNode? node) =>
        node is JsonValue value &&
        decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
}
