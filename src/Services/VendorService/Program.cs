using Microsoft.EntityFrameworkCore;
using MsContractor.BuildingBlocks.Health;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<VendorDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
});
builder.Services
    .AddOptions<VendorOptions>()
    .BindConfiguration(VendorOptions.SectionName)
    .ValidateOnStart();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<VendorOptions>, VendorOptionsValidator>();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var connectionString = builder.Configuration.GetConnectionString("Redis")
        ?? builder.Configuration["Redis:ConnectionString"]
        ?? "localhost:6379";
    return ConnectionMultiplexer.Connect(connectionString);
});
builder.Services.AddSingleton<AccessTokenProtector>();
builder.Services.AddScoped<VendorJwtValidator>();
builder.Services.AddScoped<VendorJwtReplayStore>();
builder.Services.AddScoped<VendorInstallationRepository>();
builder.Services.AddScoped<VendorInstallationService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VendorDbContext>();
    Console.WriteLine("Applying PostgreSQL migrations.");
    await dbContext.Database.MigrateAsync();

    var canConnect = await dbContext.Database.CanConnectAsync();

    Console.WriteLine(
        canConnect
            ? "PostgreSQL connection check succeeded."
            : "PostgreSQL connection check failed.");

    var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
    await redis.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine("Redis connection check succeeded.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();
