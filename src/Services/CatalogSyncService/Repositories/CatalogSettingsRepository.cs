using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ICatalogSettingsRepository
{
    Task<CatalogSettingsResponse?> GetAsync(Guid accountId, CancellationToken cancellationToken);

    Task SaveAsync(
        Guid accountId,
        bool includeArchivedWithDocuments,
        int groupLimit,
        int itemLimit,
        IReadOnlyList<CatalogDuplicateExclusionSetting> exclusions,
        CancellationToken cancellationToken);
}

public sealed class CatalogSettingsRepository : ICatalogSettingsRepository
{
    private readonly CatalogSyncDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public CatalogSettingsRepository(
        CatalogSyncDbContext dbContext,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<CatalogSettingsResponse?> GetAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var settings = await _dbContext.CatalogSettings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.AccountId == accountId, cancellationToken);
        if (settings is null)
            return null;

        var exclusions = await _dbContext.CatalogSettingExclusions.AsNoTracking()
            .Where(item => item.AccountId == accountId)
            .OrderBy(item => item.Field)
            .ThenBy(item => item.Value)
            .Select(item => new CatalogDuplicateExclusionSetting(item.Field, item.Value))
            .ToListAsync(cancellationToken);

        return new CatalogSettingsResponse(
            exclusions,
            new CatalogDuplicateSearchOptions(settings.IncludeArchivedWithDocuments),
            new CatalogDuplicateSearchLimits(settings.GroupLimit, settings.ItemLimit));
    }

    public async Task SaveAsync(
        Guid accountId,
        bool includeArchivedWithDocuments,
        int groupLimit,
        int itemLimit,
        IReadOnlyList<CatalogDuplicateExclusionSetting> exclusions,
        CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        var settings = await _dbContext.CatalogSettings.SingleOrDefaultAsync(
            item => item.AccountId == accountId,
            cancellationToken);

        if (settings is null)
        {
            settings = new CatalogSettings { AccountId = accountId };
            _dbContext.CatalogSettings.Add(settings);
        }

        settings.IncludeArchivedWithDocuments = includeArchivedWithDocuments;
        settings.GroupLimit = groupLimit;
        settings.ItemLimit = itemLimit;
        settings.UpdatedAt = _timeProvider.GetUtcNow();

        var desiredExclusions = exclusions
            .DistinctBy(item => (item.Field, item.Value))
            .ToDictionary(item => (item.Field!, item.Value!));
        var existingExclusions = await _dbContext.CatalogSettingExclusions
            .Where(item => item.AccountId == accountId)
            .ToListAsync(cancellationToken);
        var existingKeys = existingExclusions
            .Select(item => (item.Field, item.Value))
            .ToHashSet();

        _dbContext.CatalogSettingExclusions.RemoveRange(
            existingExclusions.Where(item => !desiredExclusions.ContainsKey((item.Field, item.Value))));

        foreach (var exclusion in desiredExclusions.Values)
        {
            if (existingKeys.Contains((exclusion.Field!, exclusion.Value!)))
                continue;

            _dbContext.CatalogSettingExclusions.Add(new CatalogSettingExclusion
            {
                AccountId = accountId,
                Field = exclusion.Field!,
                Value = exclusion.Value!
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
