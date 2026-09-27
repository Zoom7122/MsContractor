using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.CatalogSyncService.Repositories;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class CatalogSettingsServiceTests
{
    [Fact]
    public async Task SaveAsync_NormalizesAndPersistsSettings()
    {
        var repository = new CapturingRepository();
        var service = new CatalogSettingsService(repository);
        var accountId = Guid.NewGuid();

        await service.SaveAsync(accountId, Settings(
            [new CatalogDuplicateExclusionSetting(" EMAIL ", " shared@example.com ")],
            groupLimit: 25,
            itemLimit: 10), CancellationToken.None);

        Assert.Equal(accountId, repository.AccountId);
        using var payload = JsonDocument.Parse(repository.Payload!);
        var exclusion = Assert.Single(payload.RootElement.GetProperty("duplicateExclusions").EnumerateArray());
        Assert.Equal("email", exclusion.GetProperty("field").GetString());
        Assert.Equal("shared@example.com", exclusion.GetProperty("value").GetString());
        Assert.Equal(25, payload.RootElement.GetProperty("searchLimits").GetProperty("groupLimit").GetInt32());
        Assert.Equal(10, payload.RootElement.GetProperty("searchLimits").GetProperty("itemLimit").GetInt32());
        Assert.True(payload.RootElement.GetProperty("duplicateSearchOptions")
            .GetProperty("includeArchivedWithDocuments").GetBoolean());
    }

    [Fact]
    public async Task SaveAsync_AllowsAnEmptyExclusionList()
    {
        var repository = new CapturingRepository();
        var service = new CatalogSettingsService(repository);

        await service.SaveAsync(Guid.NewGuid(), Settings([]), CancellationToken.None);

        using var payload = JsonDocument.Parse(repository.Payload!);
        Assert.Empty(payload.RootElement.GetProperty("duplicateExclusions").EnumerateArray());
    }

    [Theory]
    [InlineData("fax", "value", 10, 10, "INVALID_DUPLICATE_EXCLUSION")]
    [InlineData("email", "  ", 10, 10, "INVALID_DUPLICATE_EXCLUSION")]
    [InlineData("phone", "value", 0, 10, "INVALID_SEARCH_LIMITS")]
    [InlineData("name", "value", 10, 1001, "INVALID_SEARCH_LIMITS")]
    public async Task SaveAsync_RejectsInvalidValuesWithoutSaving(
        string field,
        string value,
        int groupLimit,
        int itemLimit,
        string expectedCode)
    {
        var repository = new CapturingRepository();
        var service = new CatalogSettingsService(repository);

        var exception = await Assert.ThrowsAsync<CatalogSettingsValidationException>(() =>
            service.SaveAsync(Guid.NewGuid(), Settings(
                [new CatalogDuplicateExclusionSetting(field, value)],
                groupLimit,
                itemLimit), CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Null(repository.Payload);
    }

    [Fact]
    public async Task SaveAsync_UpsertsSettingsAndKeepsAccountsSeparate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var setupContext = new CatalogSyncDbContext(options);
        await setupContext.Database.EnsureCreatedAsync();

        var firstAccount = Guid.NewGuid();
        var secondAccount = Guid.NewGuid();
        await using (var firstContext = new CatalogSyncDbContext(options))
        {
            var repository = new CatalogSettingsRepository(firstContext, TimeProvider.System);
            await repository.SaveAsync(firstAccount, "{\"revision\":1}", CancellationToken.None);
            await repository.SaveAsync(firstAccount, "{\"revision\":2}", CancellationToken.None);
        }

        await using (var secondContext = new CatalogSyncDbContext(options))
        {
            var repository = new CatalogSettingsRepository(secondContext, TimeProvider.System);
            await repository.SaveAsync(secondAccount, "{\"revision\":3}", CancellationToken.None);
        }

        var rows = await setupContext.CatalogSettings.AsNoTracking()
            .ToDictionaryAsync(item => item.AccountId, item => item.Payload);
        Assert.Equal(2, rows.Count);
        Assert.Equal("{\"revision\":2}", rows[firstAccount]);
        Assert.Equal("{\"revision\":3}", rows[secondAccount]);
    }

    private static CatalogSettingsRequest Settings(
        IReadOnlyList<CatalogDuplicateExclusionSetting> exclusions,
        int groupLimit = 200,
        int itemLimit = 200) => new(
        exclusions,
        new CatalogDuplicateSearchOptions(IncludeArchivedWithDocuments: true),
        new CatalogDuplicateSearchLimits(groupLimit, itemLimit));

    private sealed class CapturingRepository : ICatalogSettingsRepository
    {
        public Guid? AccountId { get; private set; }
        public string? Payload { get; private set; }

        public Task SaveAsync(Guid accountId, string payload, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            Payload = payload;
            return Task.CompletedTask;
        }
    }
}
