using MsContractor.Gateway.Bff.Repositories;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.Logging;
using MsContractor.BuildingBlocks.OpenApi;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddMsContractorLogging();
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddMsContractorOpenApi();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6379"));
builder.Services.AddSingleton<IGatewaySessionReader, GatewaySessionReader>();
var duplicatesBaseUrl = builder.Configuration["Services:DuplicatesMergeService:BaseUrl"]
    ?? "http://localhost:5014/";
builder.Services.AddHttpClient<IDuplicatePreviewClient, DuplicatePreviewClient>(client =>
{
    client.BaseAddress = new Uri(duplicatesBaseUrl.EndsWith('/') ? duplicatesBaseUrl : $"{duplicatesBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<IMergeSelectionPreviewClient, MergeSelectionPreviewClient>(client =>
{
    client.BaseAddress = new Uri(duplicatesBaseUrl.EndsWith('/') ? duplicatesBaseUrl : $"{duplicatesBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<IMergeJobsClient, MergeJobsClient>(client =>
{
    client.BaseAddress = new Uri(duplicatesBaseUrl.EndsWith('/') ? duplicatesBaseUrl : $"{duplicatesBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
var catalogSyncBaseUrl = builder.Configuration["Services:CatalogSyncService:BaseUrl"]
    ?? "http://localhost:5013/";
builder.Services.AddHttpClient<ICatalogSyncClient, CatalogSyncClient>(client =>
{
    client.BaseAddress = new Uri(
        catalogSyncBaseUrl.EndsWith('/') ? catalogSyncBaseUrl : $"{catalogSyncBaseUrl}/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.Services.AddSingleton<IGatewaySessionRepository, GatewaySessionRepository>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.MapMsContractorOpenApi();
app.UseMsContractorSwaggerUi(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "Gateway BFF");
    options.SwaggerEndpoint("/_openapi/vendor/openapi/v1.json", "Vendor Service");
    options.SwaggerEndpoint("/_openapi/moysklad-egress/openapi/v1.json", "MoySklad Egress Service");
    options.SwaggerEndpoint("/_openapi/catalog-sync/openapi/v1.json", "Catalog Sync Service");
    options.SwaggerEndpoint("/_openapi/duplicates-merge/openapi/v1.json", "Duplicates Merge Service");
    options.SwaggerEndpoint("/_openapi/notification/openapi/v1.json", "Notification Service");
    options.SwaggerEndpoint("/_openapi/audit/openapi/v1.json", "Audit Service");
});

app.UseVendorRequestCorrelation();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapReverseProxy();
app.MapMsContractorHealth();

app.Run();
