using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MsContractor.VendorService.Middleware;

public sealed class VendorRequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<VendorRequestLoggingMiddleware> logger)
{
    private const string CorrelationIdHeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context.Request.Headers[CorrelationIdHeaderName]);
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeaderName] = correlationId;
            return Task.CompletedTask;
        });

        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["correlation_id"] = correlationId
        }))
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            logger.LogInformation(
                "VendorService request started at {StartedAtUtc}: {Method} {Path} with correlation {CorrelationId}",
                startedAtUtc,
                context.Request.Method,
                context.Request.Path,
                correlationId);

            try
            {
                await next(context);
            }
            finally
            {
                var completionLogLevel = context.Response.StatusCode switch
                {
                    >= StatusCodes.Status500InternalServerError => LogLevel.Error,
                    >= StatusCodes.Status400BadRequest => LogLevel.Warning,
                    _ => LogLevel.Information
                };
                logger.Log(
                    completionLogLevel,
                    "VendorService request completed at {CompletedAtUtc}: {Method} {Path} returned {StatusCode} in {DurationMs} ms with correlation {CorrelationId}",
                    DateTimeOffset.UtcNow,
                    context.Request.Method,
                    context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.Elapsed.TotalMilliseconds,
                    correlationId);
            }
        }
    }

    private static string GetCorrelationId(string? suppliedCorrelationId) =>
        Guid.TryParse(suppliedCorrelationId, out var correlationId)
            ? correlationId.ToString("D")
            : Guid.NewGuid().ToString("D");
}

public static class VendorRequestLoggingMiddlewareExtensions
{
    public static IApplicationBuilder UseVendorRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<VendorRequestLoggingMiddleware>();
}
