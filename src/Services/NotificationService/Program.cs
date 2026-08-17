using MsContractor.NotificationService;
using MsContractor.BuildingBlocks.Health;
using MsContractor.BuildingBlocks.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();
builder.Services.AddMsContractorOpenApi();
builder.Services.AddHostedService<Worker>();

var app = builder.Build();
app.MapMsContractorOpenApi();
app.MapMsContractorHealth();
app.Run();
