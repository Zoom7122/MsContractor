using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Consumers;
using MsContractor.DuplicatesMergeService.Messaging;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.Logging;
using MsContractor.BuildingBlocks.OpenApi;
using Microsoft.EntityFrameworkCore;
using MsContractor.DuplicatesMergeService.Services;
using MsContractor.DuplicatesMergeService.Services.Merge;
using MsContractor.DuplicatesMergeService.Services.Merge.Counterparties;
using MsContractor.DuplicatesMergeService.Services.Merge.Documents;
using MsContractor.DuplicatesMergeService.Models.Options;
using Confluent.Kafka;
using MsContractor.CatalogSyncService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddMsContractorLogging();
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
builder.Services.AddScoped<IMergeCommandValidator, MergeCommandValidator>();
builder.Services.AddScoped<IMergeOperationStateService, MergeOperationStateService>();
builder.Services.AddScoped<IMergeDocumentDiscoveryService, MergeDocumentDiscoveryService>();
builder.Services.AddSingleton(MergeDocumentExclusionOptions.Parse(
    builder.Configuration["DOCUMENTS_MERGE_EXCLUDE"]));
builder.Services.AddScoped<IMergeDocumentChangeService, MergeDocumentChangeService>();
builder.Services.AddScoped<IMergeMainCounterpartyUpdateService, MergeMainCounterpartyUpdateService>();
builder.Services.AddScoped<IMergeCounterpartyArchiveService, MergeCounterpartyArchiveService>();
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
builder.Services.AddHttpClient<IDocumentChangeEgressClient, DocumentChangeEgressClient>(client =>
{
    client.BaseAddress = new Uri(egressBaseUrl.EndsWith('/') ? egressBaseUrl : $"{egressBaseUrl}/");
    client.Timeout = builder.Configuration.GetValue<TimeSpan?>("Merge:DocumentDiscoveryTimeout")
        ?? TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<ISalesReturnRecreationSender, SalesReturnRecreationSender>();
builder.Services.AddHttpClient<ISalesReturnRecreationEgressClient, SalesReturnRecreationEgressClient>(client =>
{
    client.BaseAddress = new Uri(egressBaseUrl.EndsWith('/') ? egressBaseUrl : $"{egressBaseUrl}/");
    client.Timeout = builder.Configuration.GetValue<TimeSpan?>("Merge:DocumentDiscoveryTimeout")
        ?? TimeSpan.FromMinutes(10);
});
builder.Services.AddScoped<IPurchaseReturnRecreationSender, PurchaseReturnRecreationSender>();
builder.Services.AddHttpClient<IPurchaseReturnRecreationEgressClient, PurchaseReturnRecreationEgressClient>(client =>
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

builder.Services.AddSingleton<IMergeDeadLetterPublisher, MergeDeadLetterPublisher>();

builder.Services.AddMergeRepositories();

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
