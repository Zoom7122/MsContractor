using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.MoySklad;

/// <summary>Helpers for MoySklad <c>{"meta": {...}}</c> references.</summary>
public static class MetaReference
{
    public static JsonObject Create(string href, string type) => new()
    {
        ["meta"] = new JsonObject
        {
            ["href"] = StripQuery(href),
            ["type"] = type,
            ["mediaType"] = "application/json"
        }
    };

    /// <summary>Builds a reference to an entity returned by the API (query parameters are dropped).</summary>
    public static JsonObject To(JsonObject entity)
    {
        var href = Href(entity) ?? throw new InvalidOperationException("Entity has no meta.href.");
        var type = Type(entity) ?? throw new InvalidOperationException("Entity has no meta.type.");
        return Create(href, type);
    }

    public static string? Href(JsonNode? node) =>
        node is JsonObject obj && obj["meta"] is JsonObject meta && meta["href"] is JsonValue value &&
        value.TryGetValue<string>(out var href)
            ? StripQuery(href)
            : null;

    public static string? Type(JsonNode? node) =>
        node is JsonObject obj && obj["meta"] is JsonObject meta && meta["type"] is JsonValue value &&
        value.TryGetValue<string>(out var type)
            ? type
            : null;

    /// <summary>Returns the trailing ID segment of an entity href.</summary>
    public static string? Id(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var path = StripQuery(href).TrimEnd('/');
        var slash = path.LastIndexOf('/');
        return slash < 0 ? null : path[(slash + 1)..];
    }

    /// <summary>Compares references by entity type segment and ID, ignoring host differences and query.</summary>
    public static bool SameEntity(string? left, string? right)
    {
        if (left is null || right is null) return false;
        return string.Equals(Key(left), Key(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string href)
    {
        var path = StripQuery(href).TrimEnd('/');
        var entity = path.IndexOf("/entity/", StringComparison.Ordinal);
        return entity < 0 ? path : path[(entity + "/entity/".Length)..];
    }

    private static string StripQuery(string href)
    {
        var query = href.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? href : href[..query];
    }
}
