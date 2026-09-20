using System.Text.Json;
using MergeVerifier.Models;
using MergeVerifier.Normalization;

namespace MergeVerifier.Comparison;

public static class StructuredDiff
{
    public static IReadOnlyList<FieldDifference> Compare(JsonElement? before, JsonElement? after)
    {
        var result = new List<FieldDifference>();
        Walk(before, after, "", result);
        return result;
    }

    private static void Walk(JsonElement? before, JsonElement? after, string path, List<FieldDifference> result)
    {
        if (before.HasValue && after.HasValue && JsonCanonicalizer.Canonicalize(before.Value) == JsonCanonicalizer.Canonicalize(after.Value)) return;
        if (before?.ValueKind == JsonValueKind.Object && after?.ValueKind == JsonValueKind.Object)
        {
            var left = before.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            var right = after.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value);
            foreach (var key in left.Keys.Union(right.Keys).Order(StringComparer.Ordinal))
                Walk(left.TryGetValue(key, out var l) ? l : null, right.TryGetValue(key, out var r) ? r : null,
                    path + "/" + key.Replace("~", "~0").Replace("/", "~1"), result);
        }
        else result.Add(new(path.Length == 0 ? "/" : path, before?.Clone(), after?.Clone()));
    }
}
