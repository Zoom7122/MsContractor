using MsContractor.VendorService.Clients;
using MsContractor.VendorService.HealthChecks;
using MsContractor.VendorService.Models.Options;
using MsContractor.VendorService.Persistence;
using MsContractor.VendorService.Repositories;
using System.Net;
using Microsoft.EntityFrameworkCore;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.Logging;
using MsContractor.BuildingBlocks.OpenApi;
using MsContractor.VendorService.Middleware;
using MsContractor.VendorService.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddMsContractorLogging();
builder.AddMsContractorHealth();

builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = context =>
            new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(
                new MsContractor.VendorService.Contracts.VendorErrorResponse(
                    "VENDOR_VALIDATION_ERROR",
                    "The request body is invalid."));
    });
builder.Services.AddMsContractorOpenApi();
builder.Services.AddDbContext<VendorDbContext>(options =>
{
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("Postgres"))
        .AddInterceptors(new VendorTenantConnectionInterceptor());
});
builder.Services
    .AddOptions<VendorOptions>()
    .BindConfiguration(VendorOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<VendorOptions>, VendorOptionsValidator>();
builder.Services
    .AddOptions<DevSessionOptions>()
    .BindConfiguration(DevSessionOptions.SectionName);
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddOptions<DevSessionOptions>().ValidateOnStart();
    builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<DevSessionOptions>, DevSessionOptionsValidator>();
}
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? builder.Configuration["Redis:ConnectionString"]
        ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});
builder.Services.AddSingleton<AccessTokenProtector>();
builder.Services.AddSingleton<MoyskladVendorJwtFactory>();
builder.Services.AddHttpClient<IMoyskladContextClient, MoyskladContextClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VendorOptions>>();
    client.BaseAddress = options.Value.AppsApiBaseUrl;
    client.Timeout = TimeSpan.FromSeconds(10);
})
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip
    });
builder.Services.AddSingleton<IVendorSessionStore, VendorSessionStore>();
builder.Services.AddScoped<VendorJwtValidator>();
builder.Services.AddScoped<VendorJwtReplayStore>();
builder.Services.AddScoped<IVendorInstallationRepository, VendorInstallationRepository>();
builder.Services.AddScoped<VendorInstallationService>();
builder.Services.AddScoped<MoyskladSessionService>();
builder.Services.AddHealthChecks()
    .AddCheck<VendorReadinessHealthCheck>("vendor-dependencies", tags: ["ready"]);

builder.Services.AddSingleton<IVendorSessionRepository, VendorSessionRepository>();
builder.Services.AddSingleton<IVendorJwtReplayRepository, VendorJwtReplayRepository>();

var app = builder.Build();
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("MsContractor.VendorService.Startup");

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VendorDbContext>();
    startupLogger.LogInformation("Applying PostgreSQL migrations.");
    await dbContext.Database.MigrateAsync();

    var canConnect = await dbContext.Database.CanConnectAsync();
    if (canConnect)
        startupLogger.LogInformation("PostgreSQL connection check succeeded.");
    else
        startupLogger.LogWarning("PostgreSQL connection check failed.");

    var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
    await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(30));
    startupLogger.LogInformation("Redis connection check succeeded.");
}

app.MapMsContractorOpenApi();

app.UseHttpsRedirection();

app.UseVendorRequestLogging();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();
