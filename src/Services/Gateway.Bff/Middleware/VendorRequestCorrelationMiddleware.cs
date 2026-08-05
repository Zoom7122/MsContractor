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
    private const string MoyskladPathPrefix = "/api/moysklad/";
    private const string MoyskladPathTemplate = "/api/moysklad/{**catch-all}";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = GetCorrelationId(context.Request.Headers[CorrelationIdHeaderName]);
        context.Items[CorrelationIdItemName] = correlationId;
        // YARP copies request headers to VendorService, so propagate an id generated at the edge too.
        context.Request.Headers[CorrelationIdHeaderName] = correlationId;
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
            var isMoyskladRequest = context.Request.Path.StartsWithSegments(
                MoyskladPathPrefix,
                StringComparison.OrdinalIgnoreCase);
            logger.LogInformation(
                "Gateway request started at {StartedAtUtc}: {Method} {Path} with correlation {CorrelationId}",
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
                if (context.Request.Path.StartsWithSegments(
                        "/api/moysklad/session",
                        StringComparison.OrdinalIgnoreCase))
                {
                    var setCookieHeaders = context.Response.Headers.SetCookie;
                    var setCookiePresent = setCookieHeaders.Count > 0;
                    logger.LogInformation(
                        "Gateway session response cookie forwarding: method={Method}, status_code={StatusCode}, set_cookie_present={SetCookiePresent}, set_cookie_count={SetCookieCount}, secure={Secure}, http_only={HttpOnly}, same_site_none={SameSiteNone}",
                        context.Request.Method,
                        context.Response.StatusCode,
                        setCookiePresent,
                        setCookieHeaders.Count,
                        setCookiePresent && setCookieHeaders.Any(value =>
                            value?.Contains("Secure", StringComparison.OrdinalIgnoreCase) == true),
                        setCookiePresent && setCookieHeaders.Any(value =>
                            value?.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase) == true),
                        setCookiePresent && setCookieHeaders.Any(value =>
                            value?.Contains("SameSite=None", StringComparison.OrdinalIgnoreCase) == true));
                }

                logger.LogInformation(
                    "Gateway request completed at {CompletedAtUtc}: {Method} {Path} returned {StatusCode} in {DurationMs} ms; proxiedToVendorService={ProxiedToVendorService}; correlation {CorrelationId}",
                    DateTimeOffset.UtcNow,
                    context.Request.Method,
                    isMoyskladRequest ? MoyskladPathTemplate : context.Request.Path,
                    context.Response.StatusCode,
                    stopwatch.Elapsed.TotalMilliseconds,
                    isMoyskladRequest,
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
