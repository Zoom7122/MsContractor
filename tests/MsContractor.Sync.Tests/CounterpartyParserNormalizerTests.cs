using MsContractor.CatalogSyncService.Services;
using System.Text.Json;

namespace MsContractor.Sync.Tests;

public sealed class CounterpartyParserNormalizerTests
{
    [Fact]
    public void ParseAndNormalize_PreservesOriginalsAndRawJson()
    {
        var moySkladId = Guid.NewGuid();
        var parser = new MoySkladCounterpartyParser();
        var parsed = parser.Parse(
            $$"""
              {
                "meta": { "size": 1, "limit": 1000, "offset": 0 },
                "rows": [{
                  "id": "{{moySkladId}}",
                  "name": "  ООО   Ромашка ",
                  "phone": "+7 (999) 123-45-67",
                  "email": " SALES@EXAMPLE.COM ",
                  "inn": "77-01 23456",
                  "archived": false,
                  "unknownField": "preserved"
                }]
              }
              """);

        var entity = new CounterpartyNormalizer().Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            parsed.Rows.Single(),
            DateTimeOffset.UtcNow);

        Assert.Equal(1, parsed.Meta.Size);
        Assert.Equal(moySkladId, entity.Id);
        Assert.Equal("  ООО   Ромашка ", entity.Name);
        Assert.Equal("ооо ромашка", entity.NormalizedName);
        Assert.Equal("+7 (999) 123-45-67", entity.NormalizedPhone);
        Assert.Equal("sales@example.com", entity.NormalizedEmail);
        Assert.Equal("770123456", entity.NormalizedInn);
        using var raw = JsonDocument.Parse(entity.RawJson);
        Assert.Equal("preserved", raw.RootElement.GetProperty("unknownField").GetString());
    }

    [Theory]
    [InlineData("8 (999) 123-45-67", "+7 (999) 123-45-67")]
    [InlineData("+7 (999) 123-45-67", "+7 (999) 123-45-67")]
    [InlineData(" 8-ABC ", "+7-abc")]
    public void NormalizePhone_PreservesCharactersAndReplacesLeadingEight(string input, string expected)
    {
        Assert.Equal(expected, CounterpartyNormalizer.NormalizePhone(input));
    }

    [Fact]
    public void Parse_AllowsMissingNullableFields()
    {
        var parsed = new MoySkladCounterpartyParser().Parse(
            $$"""{"meta":{"size":1,"limit":1000,"offset":0},"rows":[{"id":"{{Guid.NewGuid()}}","name":"ИП"}]}""");

        Assert.Null(parsed.Rows.Single().Value.Email);
        Assert.Null(parsed.Rows.Single().Value.Phone);
    }

    [Fact]
    public void Parse_AcceptsMoySkladNonIsoUpdatedTimestamp()
    {
        var parsed = new MoySkladCounterpartyParser().Parse(
            $$"""{"meta":{"size":1,"limit":1000,"offset":0},"rows":[{"id":"{{Guid.NewGuid()}}","name":"ИП","updated":"2026-03-20 12:47:32.853"}]}""");

        Assert.Equal(
            new DateTimeOffset(2026, 3, 20, 9, 47, 32, 853, TimeSpan.Zero),
            parsed.Rows.Single().Value.Updated);
        Assert.Equal(TimeSpan.Zero, parsed.Rows.Single().Value.Updated?.Offset);
    }

    [Fact]
    public void Parse_PreservesArchivedCounterparties()
    {
        var parsed = new MoySkladCounterpartyParser().Parse(
            $$"""{"meta":{"size":1,"limit":1000,"offset":0},"rows":[{"id":"{{Guid.NewGuid()}}","name":"Архивный","archived":true}]}""");

        Assert.True(parsed.Rows.Single().Value.Archived);
    }
}
