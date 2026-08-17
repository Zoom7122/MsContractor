using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Services;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Controllers;

[ApiController]
[Route("api/moysklad/session")]
public sealed class MoyskladSessionController(
    MoyskladSessionService service,
    IOptions<VendorOptions> options,
    IOptions<DevSessionOptions> devSessionOptions,
    IWebHostEnvironment environment,
    TimeProvider timeProvider,
    ILogger<MoyskladSessionController> logger) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MoyskladSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] MoyskladSessionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "SESSION_VALIDATION_ERROR", "A request body is required.");

        try
        {
            Request.Cookies.TryGetValue(options.Value.SessionCookieName, out var priorToken);
            logger.LogInformation(
                "Iframe session creation started: app_id={AppId}, app_uid={AppUid}, locale={UserLocale}, prior_cookie_present={PriorCookiePresent}, external_https={ExternalHttps}",
                request.AppId,
                request.AppUid,
                request.UserLocale,
                !string.IsNullOrWhiteSpace(priorToken),
                IsExternalHttps());

            var created = await service.CreateAsync(request, priorToken, cancellationToken);
            logger.LogInformation(
                "Iframe session stored: account_id={AccountId}, employee_id={EmployeeId}, prior_session_replaced={PriorSessionReplaced}",
                created.Response.AccountId,
                created.Response.EmployeeId,
                !string.IsNullOrWhiteSpace(priorToken));

            AppendSessionCookie(
                created.Token,
                "created",
                created.Response.AccountId,
                created.Response.EmployeeId);
            return Ok(created.Response);
        }
        catch (VendorValidationException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "SESSION_VALIDATION_ERROR", exception.Message);
        }
        catch (VendorContextExpiredException)
        {
            return Error(StatusCodes.Status404NotFound, "SESSION_CONTEXT_EXPIRED", "MoySklad contextKey was not found or has expired.");
        }
        catch (VendorForbiddenException)
        {
            return Error(StatusCodes.Status403Forbidden, "SESSION_FORBIDDEN", "The application or account is not authorized.");
        }
        catch (VendorUpstreamException)
        {
            return Error(StatusCodes.Status502BadGateway, "SESSION_UPSTREAM_ERROR", "MoySklad Vendor API is unavailable.");
        }
    }

    [HttpPost("dev")]
    [ProducesResponseType<MoyskladSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateDevAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment())
            return NotFound();

        Request.Cookies.TryGetValue(options.Value.SessionCookieName, out var priorToken);
        var created = await service.CreateDevAsync(
            devSessionOptions.Value.AccountId,
            priorToken,
            cancellationToken);

        AppendSessionCookie(
            created.Token,
            "dev-created",
            created.Response.AccountId,
            created.Response.EmployeeId);
        return Ok(created.Response);
    }

    [HttpGet("me")]
    [ProducesResponseType<MoyskladSessionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MeAsync(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(options.Value.SessionCookieName, out var token);
        var session = await service.GetAsync(token, cancellationToken);
        if (session is null)
        {
            DeleteSessionCookie();
            return Error(StatusCodes.Status401Unauthorized, "SESSION_UNAUTHORIZED", "Session is missing or expired.");
        }

        AppendSessionCookie(token!, "refreshed", session.AccountId, session.EmployeeId);
        return Ok(session);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(options.Value.SessionCookieName, out var token);
        await service.LogoutAsync(token, cancellationToken);
        DeleteSessionCookie();
        logger.LogInformation(
            "Iframe session cookie cleared: cookie_name={CookieName}, cookie_was_present={CookieWasPresent}",
            options.Value.SessionCookieName,
            !string.IsNullOrWhiteSpace(token));
        return NoContent();
    }

    private void AppendSessionCookie(
        string token,
        string operation,
        Guid accountId,
        Guid employeeId)
    {
        var cookieOptions = CreateCookieOptions();
        cookieOptions.MaxAge = options.Value.SessionLifetime;
        cookieOptions.Expires = timeProvider.GetUtcNow().Add(options.Value.SessionLifetime);
        Response.Cookies.Append(options.Value.SessionCookieName, token, cookieOptions);
        logger.LogInformation(
            "Iframe session cookie appended to response: operation={Operation}, account_id={AccountId}, employee_id={EmployeeId}, cookie_name={CookieName}, set_cookie_present={SetCookiePresent}, http_only={HttpOnly}, secure={Secure}, same_site={SameSite}, path={Path}, max_age_seconds={MaxAgeSeconds}",
            operation,
            accountId,
            employeeId,
            options.Value.SessionCookieName,
            Response.Headers.SetCookie.Count > 0,
            cookieOptions.HttpOnly,
            cookieOptions.Secure,
            cookieOptions.SameSite,
            cookieOptions.Path,
            (long)options.Value.SessionLifetime.TotalSeconds);
    }

    private void DeleteSessionCookie() =>
        Response.Cookies.Delete(options.Value.SessionCookieName, CreateCookieOptions());

    private CookieOptions CreateCookieOptions()
    {
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.None,
            Path = "/",
            IsEssential = true
        };
    }

    private bool IsExternalHttps()
    {
        if (Request.IsHttps)
            return true;

        var forwardedProtocols = Request.Headers["X-Forwarded-Proto"]
            .ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return forwardedProtocols.Any(protocol =>
            string.Equals(protocol, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new VendorErrorResponse(code, message));
}
