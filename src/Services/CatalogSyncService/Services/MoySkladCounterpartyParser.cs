using System.Text.Json;
using MsContractor.CatalogSyncService.Models;

namespace MsContractor.CatalogSyncService.Services;

public interface IMoySkladCounterpartyParser
{
    ParsedCounterpartyCollection Parse(string json);
}

public sealed class MoySkladCounterpartyParser : IMoySkladCounterpartyParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public ParsedCounterpartyCollection Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("meta", out var metaElement) ||
            !root.TryGetProperty("rows", out var rowsElement) ||
            rowsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("MoySklad collection must contain meta and rows.");
        }

        var meta = metaElement.Deserialize<MoySkladMeta>(JsonOptions)
            ?? throw new JsonException("MoySklad meta is invalid.");
        var rows = new List<ParsedCounterparty>();
        foreach (var row in rowsElement.EnumerateArray())
        {
            var value = row.Deserialize<MoySkladCounterparty>(JsonOptions)
                ?? throw new JsonException("MoySklad counterparty is invalid.");
            if (value.Id == Guid.Empty || string.IsNullOrWhiteSpace(value.Name))
                throw new JsonException("MoySklad counterparty does not contain required fields.");
            rows.Add(new ParsedCounterparty(value, row.GetRawText()));
        }

        return new ParsedCounterpartyCollection(meta, rows);
    }
}
