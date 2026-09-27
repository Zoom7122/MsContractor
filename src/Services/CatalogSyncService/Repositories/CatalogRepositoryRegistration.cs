using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MsContractor.CatalogSyncService.Repositories;

public static class CatalogRepositoryRegistration
{
    public static IServiceCollection AddCatalogRepositories(this IServiceCollection services)
    {
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<ISyncRepository, SyncRepository>();
        services.AddScoped<ISyncOutboxRepository, SyncOutboxRepository>();
        services.AddScoped<ISyncRunStateRepository, SyncRunStateRepository>();
        services.AddScoped<IMergeJobStateRepository, MergeJobStateRepository>();
        services.AddScoped<ICatalogSettingsRepository, CatalogSettingsRepository>();
        return services;
    }
}
