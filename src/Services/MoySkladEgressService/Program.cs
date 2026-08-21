using System.Net;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.OpenApi;
using MsContractor.MoySkladEgressService.Services;
using Npgsql;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(egressOptions));
builder.Services.AddSingleton(documentChangeOptions);
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
builder.Services.AddScoped<IMoySkladDocumentDiscoveryService, MoySkladDocumentDiscoveryService>();
builder.Services.AddScoped<IMoySkladDocumentChangeService, MoySkladDocumentChangeService>();
builder.Services.AddSingleton<IMoySkladRateLimiter, ObservingMoySkladRateLimiter>();
builder.Services.AddHealthChecks()
    .AddCheck<EgressReadinessHealthCheck>("egress-dependencies", tags: ["ready"]);

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("MsContractor.MoySkladEgressService.Startup");
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrWhiteSpace(postgresConnectionString))
{
    try
    {
        await using var connection = new NpgsqlConnection(postgresConnectionString);
        await connection.OpenAsync();
        startupLogger.LogInformation("PostgreSQL connection check succeeded.");
    }
    catch (Exception exception)
    {
        // PostgreSQL is not a runtime dependency of Egress; keep startup resilient,
        // but make an unavailable configured database visible to operators.
        startupLogger.LogWarning(exception, "PostgreSQL connection check failed.");
    }
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
