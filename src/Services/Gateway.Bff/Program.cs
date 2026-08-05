using MsContractor.BuildingBlocks.Health;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6379"));
builder.Services.AddSingleton<IGatewaySessionReader, GatewaySessionReader>();
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseVendorRequestCorrelation();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapReverseProxy();
app.MapMsContractorHealth();

app.Run();
