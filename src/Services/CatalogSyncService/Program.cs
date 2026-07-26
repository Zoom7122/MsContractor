using MsContractor.CatalogSyncService;
using MsContractor.BuildingBlocks.Health;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.MapMsContractorHealth();
app.Run();
