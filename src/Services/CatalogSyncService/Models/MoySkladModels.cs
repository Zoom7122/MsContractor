using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsContractor.CatalogSyncService.Models;

public sealed class MoySkladMeta
{
    public int Size { get; init; }
    public int Limit { get; init; }
    public int Offset { get; init; }
    public string? NextHref { get; init; }
    public string? PreviousHref { get; init; }
}

public sealed class MoySkladCounterparty
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public string? Inn { get; init; }
    public string? Kpp { get; init; }
    public string? Description { get; init; }
    public bool Archived { get; init; }
    [JsonConverter(typeof(MoySkladDateTimeOffsetConverter))]
    public DateTimeOffset? Updated { get; init; }
    public JsonElement? Attributes { get; init; }
}

public sealed record ParsedCounterparty(MoySkladCounterparty Value, string RawJson);

public sealed record ParsedCounterpartyCollection(
    MoySkladMeta Meta,
    IReadOnlyList<ParsedCounterparty> Rows);
