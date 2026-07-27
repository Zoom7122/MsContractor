using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MsContractor.Gateway.Bff.Middleware;

public sealed class VendorRequestCorrelationMiddleware(
    RequestDelegate next,
    ILogger<VendorRequestCorrelationMiddleware> logger)
{
    public const string CorrelationIdHeaderName = "X-Correlation-Id";
    public const string CorrelationIdItemName = "CorrelationId";
    private const string VendorPathPrefix = "/api/moysklad/vendor/";
    private const string VendorPathTemplate = "/api/moysklad/vendor/{**catch-all}";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context.Request.Headers[CorrelationIdHeaderName]);
        context.Items[CorrelationIdItemName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationId
        }))
        {
            if (!context.Request.Path.StartsWithSegments(VendorPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                logger.LogInformation(
                    "Vendor API request proxied: {Method} {PathTemplate} returned {StatusCode} in {DurationMs} ms to {DestinationService} with correlation {CorrelationId}",
                    context.Request.Method,
                    VendorPathTemplate,
                    context.Response.StatusCode,
                    stopwatch.Elapsed.TotalMilliseconds,
                    "vendor-service",
                    correlationId);
            }
        }
    }

    private static string GetCorrelationId(string? suppliedCorrelationId) =>
        Guid.TryParse(suppliedCorrelationId, out var correlationId)
            ? correlationId.ToString("D")
            : Guid.NewGuid().ToString("D");
}

public static class VendorRequestCorrelationMiddlewareExtensions
{
    public static IApplicationBuilder UseVendorRequestCorrelation(this IApplicationBuilder app) =>
        app.UseMiddleware<VendorRequestCorrelationMiddleware>();
}
