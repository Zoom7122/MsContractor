using Microsoft.EntityFrameworkCore;
using MsContractor.BuildingBlocks.Health;
using MsContractor.VendorService.Repo;

var builder = WebApplication.CreateBuilder(args);
builder.AddMsContractorHealth();

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<VendorDbContext>(options =>
{
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres"));
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapMsContractorHealth();

app.Run();
