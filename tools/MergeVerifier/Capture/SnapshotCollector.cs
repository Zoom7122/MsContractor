using System.Text.Json;
using MergeVerifier.Client;
using MergeVerifier.Models;
using MergeVerifier.Normalization;

namespace MergeVerifier.Capture;

public sealed class SnapshotCollector(EgressClient client)
{
    public async Task<MergeSnapshot> CollectAsync(Guid accountId, Guid mainId, IReadOnlyList<Guid> duplicateIds,
        CancellationToken cancellationToken)
    {
        var allIds = new[] { mainId }.Concat(duplicateIds).ToArray();
        var result = await client.CaptureAsync(accountId, allIds, cancellationToken);
        if (result.Counterparties.Select(item => item.CounterpartyId).ToHashSet().Count != allIds.Length ||
            !result.Counterparties.Select(item => item.CounterpartyId).ToHashSet().SetEquals(allIds))
            throw new InvalidOperationException("Egress returned an incomplete counterparty capture.");

        var counterparties = result.Counterparties.Select(item => new CounterpartySnapshot(item.CounterpartyId,
            item.CounterpartyId == mainId ? "Main" : "Duplicate", ReadArchived(item.RawJson), item.RawJson,
            DocumentNormalizer.NormalizeCounterparty(item.RawJson))).ToArray();
        var documents = result.Documents.Select(item =>
        {
            var normalized = DocumentNormalizer.Normalize(item.DocumentType, item.RawJson, item.PositionRawJson,
                recreated: IsRecreated(item.DocumentType));
            return new DocumentSnapshot(item.DocumentType, item.DocumentId, item.CounterpartyId,
                ReadString(item.RawJson, "name"), ReadString(item.RawJson, "externalCode"), ReadString(item.RawJson, "moment"),
                ReadDecimal(item.RawJson, "sum"), ReadAgentId(item.RawJson), item.PositionRawJson, item.RawJson,
                normalized.Json, normalized.Hash);
        }).ToArray();

        return new MergeSnapshot("1", accountId, mainId, duplicateIds, DateTimeOffset.UtcNow, counterparties, documents,
            result.DocumentTypes);
    }

    public static bool IsRecreated(string type) => string.Equals(type, "salesreturn", StringComparison.Ordinal);

    private static bool? ReadArchived(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("archived", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    }
    private static string? ReadString(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
    private static decimal? ReadDecimal(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.TryGetDecimal(out var result) ? result : null;
    }
    private static Guid? ReadAgentId(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.Object ||
            !agent.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("href", out var href) ||
            href.ValueKind != JsonValueKind.String || !Uri.TryCreate(href.GetString(), UriKind.Absolute, out var uri)) return null;
        return Guid.TryParse(uri.AbsolutePath.TrimEnd('/').Split('/').Last(), out var value) ? value : null;
    }
}
