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
    public async Task GetAsync_ReturnsDefaultsWhenAccountHasNoSavedSettings()
    {
        var accountId = Guid.NewGuid();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var setupContext = new CatalogSyncDbContext(options);
        await setupContext.Database.EnsureCreatedAsync();
        await using var readContext = new CatalogSyncDbContext(options);
        var repository = new CatalogSettingsRepository(readContext, TimeProvider.System);
        var service = new CatalogSettingsService(repository);

        var settings = await service.GetAsync(accountId, CancellationToken.None);

        Assert.Empty(settings.DuplicateExclusions);
        Assert.True(settings.DuplicateSearchOptions.IncludeArchivedWithDocuments);
        Assert.Equal(200, settings.SearchLimits.GroupLimit);
        Assert.Equal(200, settings.SearchLimits.ItemLimit);
    }

    [Fact]
    public async Task SaveAsync_NormalizesAndPersistsSettings()
    {
        var repository = new CapturingRepository();
        var service = new CatalogSettingsService(repository);
        var accountId = Guid.NewGuid();

        await service.SaveAsync(accountId, Settings(
            [
                new CatalogDuplicateExclusionSetting(" EMAIL ", " shared@example.com "),
                new CatalogDuplicateExclusionSetting("email", "SHARED@example.com")
            ],
            groupLimit: 25,
            itemLimit: 10), CancellationToken.None);

        Assert.True(repository.WasCalled);
        Assert.Equal(accountId, repository.AccountId);
        Assert.True(repository.IncludeArchivedWithDocuments);
        Assert.Equal(25, repository.GroupLimit);
        Assert.Equal(10, repository.ItemLimit);
        var exclusion = Assert.Single(repository.Exclusions!);
        Assert.Equal("email", exclusion.Field);
        Assert.Equal("shared@example.com", exclusion.Value);
    }

    [Fact]
    public async Task SaveAsync_AllowsAnEmptyExclusionList()
    {
        var repository = new CapturingRepository();
        var service = new CatalogSettingsService(repository);

        await service.SaveAsync(Guid.NewGuid(), Settings([]), CancellationToken.None);

        Assert.True(repository.WasCalled);
        Assert.Empty(repository.Exclusions!);
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
        Assert.False(repository.WasCalled);
    }

    [Fact]
    public async Task SaveAsync_ReplacesSettingsAndExclusionsPerAccount()
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
            await repository.SaveAsync(
                firstAccount,
                true,
                25,
                10,
                [new CatalogDuplicateExclusionSetting("email", "shared@example.com"),
                 new CatalogDuplicateExclusionSetting("phone", "+70000000000")],
                CancellationToken.None);
            await repository.SaveAsync(
                firstAccount,
                false,
                30,
                12,
                [new CatalogDuplicateExclusionSetting("name", "acme"),
                 new CatalogDuplicateExclusionSetting("email", "shared@example.com"),
                 new CatalogDuplicateExclusionSetting("email", "shared@example.com")],
                CancellationToken.None);
        }

        await using (var secondContext = new CatalogSyncDbContext(options))
        {
            var repository = new CatalogSettingsRepository(secondContext, TimeProvider.System);
            await repository.SaveAsync(
                secondAccount,
                true,
                40,
                20,
                [new CatalogDuplicateExclusionSetting("email", "second@example.com")],
                CancellationToken.None);
        }

        var rows = await setupContext.CatalogSettings.AsNoTracking()
            .ToDictionaryAsync(item => item.AccountId);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[firstAccount].IncludeArchivedWithDocuments);
        Assert.Equal(30, rows[firstAccount].GroupLimit);
        Assert.Equal(12, rows[firstAccount].ItemLimit);
        Assert.Equal(40, rows[secondAccount].GroupLimit);

        var exclusions = await setupContext.CatalogSettingExclusions.AsNoTracking()
            .OrderBy(item => item.AccountId)
            .ThenBy(item => item.Field)
            .ToListAsync();
        Assert.Equal(3, exclusions.Count);
        Assert.Equal(
            [("email", "shared@example.com"), ("name", "acme")],
            exclusions.Where(item => item.AccountId == firstAccount)
                .Select(item => (item.Field, item.Value))
                .OrderBy(item => item.Field)
                .ToArray());
        var secondAccountExclusion = Assert.Single(exclusions, item => item.AccountId == secondAccount);
        Assert.Equal("second@example.com", secondAccountExclusion.Value);

        await using var firstReadContext = new CatalogSyncDbContext(options);
        var firstSettings = await new CatalogSettingsRepository(firstReadContext, TimeProvider.System)
            .GetAsync(firstAccount, CancellationToken.None);
        Assert.NotNull(firstSettings);
        Assert.False(firstSettings.DuplicateSearchOptions.IncludeArchivedWithDocuments);
        Assert.Equal(30, firstSettings.SearchLimits.GroupLimit);
        Assert.Equal(12, firstSettings.SearchLimits.ItemLimit);
        Assert.Equal(
            [("email", "shared@example.com"), ("name", "acme")],
            firstSettings.DuplicateExclusions
                .Select(item => (item.Field, item.Value))
                .OrderBy(item => item.Field)
                .ToArray());

        await using var secondReadContext = new CatalogSyncDbContext(options);
        var secondSettings = await new CatalogSettingsRepository(secondReadContext, TimeProvider.System)
            .GetAsync(secondAccount, CancellationToken.None);
        Assert.NotNull(secondSettings);
        Assert.True(secondSettings.DuplicateSearchOptions.IncludeArchivedWithDocuments);
        Assert.Equal(40, secondSettings.SearchLimits.GroupLimit);
        var onlySecondAccountExclusion = Assert.Single(secondSettings.DuplicateExclusions);
        Assert.Equal("second@example.com", onlySecondAccountExclusion.Value);
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
        public bool IncludeArchivedWithDocuments { get; private set; }
        public int GroupLimit { get; private set; }
        public int ItemLimit { get; private set; }
        public IReadOnlyList<CatalogDuplicateExclusionSetting>? Exclusions { get; private set; }
        public bool WasCalled { get; private set; }

        public Task<CatalogSettingsResponse?> GetAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogSettingsResponse?>(null);

        public Task SaveAsync(
            Guid accountId,
            bool includeArchivedWithDocuments,
            int groupLimit,
            int itemLimit,
            IReadOnlyList<CatalogDuplicateExclusionSetting> exclusions,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            AccountId = accountId;
            IncludeArchivedWithDocuments = includeArchivedWithDocuments;
            GroupLimit = groupLimit;
            ItemLimit = itemLimit;
            Exclusions = exclusions.ToArray();
            return Task.CompletedTask;
        }
    }
}
