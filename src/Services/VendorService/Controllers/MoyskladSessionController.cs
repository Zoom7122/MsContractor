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
    IWebHostEnvironment environment,
    TimeProvider timeProvider) : ControllerBase
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
            var created = await service.CreateAsync(request, priorToken, cancellationToken);
            AppendSessionCookie(created.Token);
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

        AppendSessionCookie(token!);
        return Ok(session);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> LogoutAsync(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(options.Value.SessionCookieName, out var token);
        await service.LogoutAsync(token, cancellationToken);
        DeleteSessionCookie();
        return NoContent();
    }

    private void AppendSessionCookie(string token)
    {
        var cookieOptions = CreateCookieOptions();
        cookieOptions.MaxAge = options.Value.SessionLifetime;
        cookieOptions.Expires = timeProvider.GetUtcNow().Add(options.Value.SessionLifetime);
        Response.Cookies.Append(options.Value.SessionCookieName, token, cookieOptions);
    }

    private void DeleteSessionCookie() =>
        Response.Cookies.Delete(options.Value.SessionCookieName, CreateCookieOptions());

    private CookieOptions CreateCookieOptions() =>
        new()
        {
            HttpOnly = true,
            Secure = !environment.IsDevelopment(),
            SameSite = environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.None,
            Path = "/",
            IsEssential = true
        };

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new VendorErrorResponse(code, message));
}
