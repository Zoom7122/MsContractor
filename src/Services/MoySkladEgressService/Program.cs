using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.HealthChecks;
using MsContractor.MoySkladEgressService.Models.Options;
using System.Net;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.Logging;
using MsContractor.BuildingBlocks.OpenApi;
using MsContractor.MoySkladEgressService.Services;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddMsContractorLogging();
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddMsContractorOpenApi();
var egressOptions = new EgressOptions
{
    JsonApiBaseUrl = NormalizeBaseUri(
        builder.Configuration["MoySklad:JsonApiBaseUrl"],
        "https://api.moysklad.ru/api/remap/1.2/"),
    VendorServiceBaseUrl = NormalizeBaseUri(
        builder.Configuration["Services:VendorService:BaseUrl"],
        "http://localhost:5011/"),
    InternalApiKey = builder.Configuration["InternalApi:Key"] ?? string.Empty
};
var documentChangeOptions = MoySkladDocumentChangeOptions.Parse(
    builder.Configuration["DOCUMENTS_PUT_CHANGE"]);
var agentAndContractOptions = MoySkladDocumentAgentAndContractOptions.Parse(
    builder.Configuration["DOCUMENTS_CHANGE_AGENT_AND_CONTRACT"]);
var documentDiscoveryOptions = MoySkladDocumentDiscoveryOptions.Parse(
    builder.Configuration["DOCUMENTS_DISCOVERY"]);
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(egressOptions));
builder.Services.AddSingleton(documentChangeOptions);
builder.Services.AddSingleton(agentAndContractOptions);
builder.Services.AddSingleton(documentDiscoveryOptions);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6379"));
builder.Services.AddHttpClient<IVendorTokenClient, VendorTokenClient>(client =>
{
    client.BaseAddress = egressOptions.VendorServiceBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<IMoySkladCounterpartyGateway, MoySkladCounterpartyGateway>(client =>
{
    client.BaseAddress = egressOptions.JsonApiBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip
});
builder.Services.AddHttpClient<IMoySkladDocumentGateway, MoySkladDocumentGateway>(client =>
{
    client.BaseAddress = egressOptions.JsonApiBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AutomaticDecompression = DecompressionMethods.GZip
});
builder.Services.AddHttpClient<IMoySkladSalesReturnGateway, MoySkladSalesReturnGateway>(client =>
{
    client.BaseAddress = egressOptions.JsonApiBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.GZip });
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for the Egress operation journal.");
builder.Services.AddDbContext<EgressDbContext>(options => options.UseNpgsql(postgresConnectionString,
    postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "egress")));
builder.Services.AddScoped<ISalesReturnOperationRepository, SalesReturnOperationRepository>();
builder.Services.AddScoped<SalesReturnPayloadBuilder>();
builder.Services.AddScoped<SalesReturnRecreationService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SalesReturnRecoveryWorker>();
builder.Services.AddSingleton<IMoySkladResponseHandler, MoySkladResponseHandler>();
builder.Services.AddSingleton<IMoySkladSingleDocumentResponseValidator, MoySkladSingleDocumentResponseValidator>();
builder.Services.AddSingleton<IMoySkladBulkDocumentResponseValidator, MoySkladBulkDocumentResponseValidator>();
builder.Services.AddScoped<IMoySkladDocumentDiscoveryService, MoySkladDocumentDiscoveryService>();
builder.Services.AddScoped<IMoySkladDocumentChangeService, MoySkladDocumentChangeService>();
builder.Services.AddScoped<IMoySkladDocumentAgentAndContractService, MoySkladDocumentAgentAndContractService>();
builder.Services.AddSingleton<IMoySkladRateLimiter, ObservingMoySkladRateLimiter>();
builder.Services.AddHealthChecks()
    .AddCheck<EgressReadinessHealthCheck>("egress-dependencies", tags: ["ready"]);

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("MsContractor.MoySkladEgressService.Startup");
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<EgressDbContext>().Database.MigrateAsync();
}

try
{
    var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
    await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(10));
    startupLogger.LogInformation("Redis connection check succeeded.");
}
catch (Exception exception)
{
    startupLogger.LogWarning(exception, "Redis connection check failed.");
}

// Configure the HTTP request pipeline.
app.MapMsContractorOpenApi();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();

static Uri NormalizeBaseUri(string? configured, string fallback)
{
    var value = string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    return new Uri(value.EndsWith('/') ? value : $"{value}/", UriKind.Absolute);
}
