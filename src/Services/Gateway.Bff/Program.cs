using MsContractor.BuildingBlocks.Health;
using MsContractor.Gateway.Bff.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
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
