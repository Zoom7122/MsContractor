using System.Text.Json.Nodes;

namespace MergeVerifier.MoySklad;

public static class MoySkladPaginationReader
{
    public static async Task<IReadOnlyList<JsonObject>> ReadAsync(IMoySkladClient client, string path, CancellationToken ct)
    {
        var result = new List<JsonObject>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        int? expectedSize = null;
        var first = new Uri(client.BaseUrl, path);
        // Official files API permits identical files with identical IDs; retain their multiplicity.
        var repeatedIdsAllowed = first.AbsolutePath.EndsWith("/files", StringComparison.Ordinal);
        var next = SetPaging(first, 0, 1000);
        while (true)
        {
            if (!visited.Add(next)) throw new VerifierException("Repeated pagination URL; dataset is incomplete.");
            var page = await client.GetAsync(next, ct);
            if (page["meta"] is not JsonObject meta || page["rows"] is not JsonArray rows)
                throw new VerifierException("Collection lacks meta/rows; dataset is incomplete.");
            var size = Integer(meta, "size");
            var offset = Integer(meta, "offset");
            var limit = Integer(meta, "limit");
            if (size < 0 || limit < 1 || offset != result.Count || rows.Count > limit ||
                result.Count + rows.Count > size || (expectedSize.HasValue && expectedSize != size))
                throw new VerifierException("Inconsistent pagination metadata; capture must be repeated while data is stable.");
            expectedSize = size;
            foreach (var node in rows)
            {
                if (node is not JsonObject row) throw new VerifierException("Invalid collection row.");
                if (!repeatedIdsAllowed && row["id"] is JsonValue id && !ids.Add(id.ToString()))
                    throw new VerifierException("Repeated entity in pagination; dataset is incomplete.");
                result.Add((JsonObject)row.DeepClone());
            }
            var href = meta["nextHref"]?.GetValue<string>();
            if (result.Count == size)
            {
                if (!string.IsNullOrEmpty(href)) throw new VerifierException("Unexpected next page beyond collection size.");
                return result;
            }
            if (rows.Count == 0) throw new VerifierException("Empty page before end of collection.");
            if (href is not null)
            {
                var candidate = new Uri(client.BaseUrl, href);
                var expected = new Uri(SetPaging(first, result.Count, limit));
                // nextHref must retain endpoint and filters, not just stay on the same host.
                if (candidate.GetLeftPart(UriPartial.Path) != expected.GetLeftPart(UriPartial.Path) ||
                    CanonicalQuery(candidate) != CanonicalQuery(expected))
                    throw new VerifierException("Next page changed scope or offset; dataset is incomplete.");
                next = candidate.AbsoluteUri;
            }
            else next = SetPaging(first, result.Count, limit);
        }
    }

    private static int Integer(JsonObject meta, string key) => meta[key]?.GetValue<int>()
        ?? throw new VerifierException("Pagination metadata field is missing.");

    private static string CanonicalQuery(Uri uri) => string.Join('&', uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(Uri.UnescapeDataString).Order(StringComparer.Ordinal));

    private static string SetPaging(Uri uri, int offset, int limit)
    {
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.StartsWith("offset=", StringComparison.Ordinal) && !x.StartsWith("limit=", StringComparison.Ordinal));
        return new UriBuilder(uri) { Query = string.Join('&', parts.Append($"limit={limit}").Append($"offset={offset}")) }.Uri.AbsoluteUri;
    }
}
