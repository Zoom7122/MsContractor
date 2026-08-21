using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.OpenApi;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.DuplicatesMergeService.Services;
using Confluent.Kafka;
using MsContractor.CatalogSyncService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddDbContext<CatalogSyncDbContext>(options =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .AddInterceptors(new CatalogTenantConnectionInterceptor()));
builder.Services.AddScoped<IDuplicatePreviewService, DuplicatePreviewService>();
builder.Services.AddScoped<IMergeSelectionPreviewService, MergeSelectionPreviewService>();
builder.Services.AddScoped<IMergeJobCreator, MergeJobCreator>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IMoySkladCounterpartyParser, MoySkladCounterpartyParser>();
builder.Services.AddSingleton<ICounterpartyNormalizer, CounterpartyNormalizer>();
builder.Services.AddScoped<IMergeProcessor, MergeProcessor>();
var egressBaseUrl = builder.Configuration["Services:MoySkladEgressService:BaseUrl"] ?? "http://localhost:5012/";
builder.Services.AddHttpClient<IMergeEgressClient, MergeEgressClient>(client =>
{
    client.BaseAddress = new Uri(egressBaseUrl.EndsWith('/') ? egressBaseUrl : $"{egressBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(45);
});
builder.Services.AddHttpClient<IDocumentDiscoveryEgressClient, DocumentDiscoveryEgressClient>(client =>
{
    client.BaseAddress = new Uri(egressBaseUrl.EndsWith('/') ? egressBaseUrl : $"{egressBaseUrl}/");
    client.Timeout = builder.Configuration.GetValue<TimeSpan?>("Merge:DocumentDiscoveryTimeout")
        ?? TimeSpan.FromMinutes(10);
});
builder.Services.AddSingleton<IProducer<string, string>>(_ =>
    new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = builder.Configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
        Acks = Acks.All,
        EnableIdempotence = true
    }).Build());
builder.Services.AddHostedService<MergeOutboxPublisher>();
builder.Services.AddHostedService<MergeRequestedConsumer>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddMsContractorOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
    await dbContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
app.MapMsContractorOpenApi();
app.UseMsContractorSwaggerUi(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Duplicates Merge Service");
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();
