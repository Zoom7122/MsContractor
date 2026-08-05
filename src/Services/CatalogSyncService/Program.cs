using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using MsContractor.BuildingBlocks.Health;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.CatalogSyncService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();
builder.Services.AddDbContext<CatalogSyncDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IMoySkladCounterpartyParser, MoySkladCounterpartyParser>();
builder.Services.AddSingleton<ICounterpartyNormalizer, CounterpartyNormalizer>();
builder.Services.AddScoped<ISyncProcessor, SyncProcessor>();
var egressBaseUrl = builder.Configuration["Services:MoySkladEgressService:BaseUrl"]
    ?? "http://localhost:5012/";
builder.Services.AddHttpClient<IMoySkladEgressClient, MoySkladEgressClient>(client =>
{
    client.BaseAddress = new Uri(
        egressBaseUrl.EndsWith('/') ? egressBaseUrl : $"{egressBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
        Acks = Acks.All,
        EnableIdempotence = true
    }).Build());
builder.Services.AddSingleton<IAdminClient>(_ =>
    new AdminClientBuilder(new AdminClientConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092"
    }).Build());
builder.Services.AddHostedService<SyncRequestedConsumer>();
builder.Services.AddHostedService<SyncOutboxPublisher>();
builder.Services.AddHealthChecks()
    .AddCheck<CatalogReadinessHealthCheck>("catalog-dependencies", tags: ["ready"]);

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
    await dbContext.Database.MigrateAsync();
}
app.MapMsContractorHealth();
app.Run();
