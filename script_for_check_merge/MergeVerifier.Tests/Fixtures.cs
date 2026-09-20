using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.Normalization;

namespace MergeVerifier.Tests;

internal static class Fixtures
{
    public static readonly Guid Main = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid Duplicate = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid Id = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid OtherId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Uri BaseUrl = new("https://api.moysklad.ru/api/remap/1.2/");
    public static JsonObject Reference(string type, Guid id) => new()
    {
        ["meta"] = new JsonObject { ["type"] = type, ["href"] = new Uri(BaseUrl, $"entity/{type}/{id}").AbsoluteUri }
    };
    public static JsonObject Position(int quantity = 1) => new()
    {
        ["id"] = Guid.NewGuid().ToString(), ["accountId"] = Guid.NewGuid().ToString(),
        ["meta"] = new JsonObject { ["type"] = "customerorderposition" },
        ["assortment"] = Reference("product", OtherId), ["quantity"] = quantity,
        ["price"] = 100, ["vat"] = 20, ["reserve"] = 0
    };
    public static JsonObject Raw(string type = "customerorder", Guid? id = null, Guid? agent = null, params int[] quantities)
    {
        var actualId = id ?? Id;
        var result = new JsonObject
        {
            ["id"] = actualId.ToString(), ["accountId"] = Guid.NewGuid().ToString(),
            ["meta"] = Reference(type, actualId)["meta"]!.DeepClone(),
            ["href"] = new Uri(BaseUrl, $"entity/{type}/{actualId}").AbsoluteUri,
            ["uuidHref"] = $"https://online.moysklad.ru/app/#edit?id={actualId}",
            ["agent"] = Reference("counterparty", agent ?? Duplicate),
            ["organization"] = Reference("organization", OtherId),
            ["name"] = "00041", ["code"] = "code", ["externalCode"] = "external",
            ["moment"] = "2026-09-20 12:00:00.000", ["sum"] = 100,
            ["updated"] = "2026-09-20 12:01:00.000", ["created"] = "2026-09-19 12:00:00.000",
            ["applicable"] = true
        };
        foreach (var collection in DocumentRegistry.Get(type).PositionCollections)
            result[collection] = new JsonArray((quantities.Length == 0 ? [1] : quantities).Select(x => (JsonNode)Position(x)).ToArray());
        return result;
    }
    public static async Task<DocumentSnapshot> Document(JsonObject raw, string type = "customerorder",
        RecreatedReferenceResolver? resolver = null)
    {
        var rule = DocumentRegistry.Get(type);
        var normalized = await DocumentNormalizer.For(rule).NormalizeAsync(raw,
            resolver ?? ((_, _, _) => throw new InvalidOperationException("Unexpected reference resolution")), default);
        return new()
        {
            EntityType = type, TransferMode = rule.TransferMode,
            SourceCounterpartyId = Capture.SnapshotCollector.ReadAgent(raw),
            StableDocumentId = rule.TransferMode == DocumentTransferMode.Recreate ? null : raw["id"]!.GetValue<string>(),
            Data = JsonSerializer.SerializeToElement(normalized)
        };
    }
    public static MergeSnapshot Snapshot(params DocumentSnapshot[] documents) => new()
    {
        MainCounterpartyId = Main, DuplicateCounterpartyIds = [Duplicate],
        CaptureStartedAt = DateTimeOffset.Parse("2026-09-20T12:00:00Z"),
        CaptureCompletedAt = DateTimeOffset.Parse("2026-09-20T12:01:00Z"), Documents = documents,
        Coverage = DocumentRegistry.All.Select(x => new CaptureCoverage(x.EntityType, x.AgentFilterSupported)).ToArray()
    };
}
