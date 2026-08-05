using Confluent.Kafka;
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
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration["Redis:ConnectionString"]
        ?? builder.Configuration.GetConnectionString("Redis")
        ?? "localhost:6379"));
builder.Services.AddSingleton<IGatewaySessionReader, GatewaySessionReader>();
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
builder.Services.AddSingleton<ISyncCommandPublisher, SyncCommandPublisher>();
builder.Services.AddHealthChecks()
    .AddCheck<KafkaReadinessHealthCheck>("kafka", tags: ["ready"]);
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
