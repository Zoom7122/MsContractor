using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;

namespace MsContractor.CatalogSyncService.Repositories;

public interface ICatalogSettingsRepository
{
    Task SaveAsync(Guid accountId, string payload, CancellationToken cancellationToken);
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

    public async Task SaveAsync(
        Guid accountId,
        string payload,
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

        settings.Payload = payload;
        settings.UpdatedAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
