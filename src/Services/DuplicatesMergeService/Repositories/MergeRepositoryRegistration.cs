namespace MsContractor.DuplicatesMergeService.Repositories;

public static class MergeRepositoryRegistration
{
    public static IServiceCollection AddMergeRepositories(this IServiceCollection services)
    {
        services.AddScoped<IMergeRepository, MergeRepository>();
        services.AddScoped<ICounterpartyRepository, CounterpartyRepository>();
        services.AddScoped<IDocumentSnapshotRepository, DocumentSnapshotRepository>();
        services.AddScoped<IMergeOutboxRepository, MergeOutboxRepository>();
        return services;
    }
}
