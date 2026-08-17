using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Swashbuckle.AspNetCore.SwaggerUI;

namespace MsContractor.BuildingBlocks.OpenApi;

public static class MsContractorOpenApiExtensions
{
    public static IServiceCollection AddMsContractorOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi();
        return services;
    }

    public static WebApplication MapMsContractorOpenApi(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
            return app;

        app.MapOpenApi();
        return app;
    }

    public static WebApplication UseMsContractorSwaggerUi(
        this WebApplication app,
        Action<SwaggerUIOptions>? configure = null)
    {
        if (!app.Environment.IsDevelopment())
            return app;

        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "swagger";
            configure?.Invoke(options);
        });

        return app;
    }
}
