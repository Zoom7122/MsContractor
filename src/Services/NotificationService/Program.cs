using MsContractor.NotificationService.Services;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.Logging;
using MsContractor.BuildingBlocks.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddMsContractorLogging();
builder.AddMsContractorHealth();
builder.Services.AddMsContractorOpenApi();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.MapMsContractorOpenApi();
app.MapMsContractorHealth();
app.Run();
