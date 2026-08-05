using MsContractor.BuildingBlocks.Health;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.DuplicatesMergeService.Services;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddDbContext<CatalogSyncDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));
builder.Services.AddScoped<IDuplicatePreviewService, DuplicatePreviewService>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();
