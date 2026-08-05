using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsContractor.CatalogSyncService.Models;

/// <summary>
/// Parses timestamps returned by MoySklad, including its timezone-less
/// <c>yyyy-MM-dd HH:mm:ss.fff</c> representation. Values without an offset
/// are interpreted in the fixed MSK offset used by the MoySklad JSON API.
/// </summary>
public sealed class MoySkladDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string MoySkladFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";
    private static readonly TimeSpan MoscowOffset = TimeSpan.FromHours(3);

    public override DateTimeOffset Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("MoySklad timestamp must be a string.");

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("MoySklad timestamp must not be empty.");

        if (DateTime.TryParseExact(
                value,
                MoySkladFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var moySkladTimestamp))
        {
            return new DateTimeOffset(
                    DateTime.SpecifyKind(moySkladTimestamp, DateTimeKind.Unspecified),
                    MoscowOffset)
                .ToUniversalTime();
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var isoTimestamp))
        {
            return isoTimestamp.ToUniversalTime();
        }

        throw new JsonException($"Invalid MoySklad timestamp: '{value}'.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
