using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/catalog/settings")]
public sealed class CatalogSettingsController : ControllerBase
{
    private readonly IGatewaySessionReader _sessionReader;
    private readonly ICatalogSettingsClient _settingsClient;
    private readonly IConfiguration _configuration;

    public CatalogSettingsController(
        IGatewaySessionReader sessionReader,
        ICatalogSettingsClient settingsClient,
        IConfiguration configuration)
    {
        _sessionReader = sessionReader;
        _settingsClient = settingsClient;
        _configuration = configuration;
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SaveAsync(
        [FromBody] CatalogSettingsRequest? settings,
        CancellationToken cancellationToken)
    {
        var cookieName = _configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await _sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new InternalErrorResponse("SESSION_UNAUTHORIZED", "Session is missing or expired."));

        if (settings is null)
            return BadRequest(new InternalErrorResponse("INVALID_CATALOG_SETTINGS", "Settings payload is required."));

        try
        {
            await _settingsClient.SaveSettingsAsync(session.AccountId, settings, cancellationToken);
            return NoContent();
        }
        catch (CatalogSettingsRejectedException exception)
        {
            return BadRequest(new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new InternalErrorResponse("CATALOG_SYNC_UNAVAILABLE", "Synchronization service is unavailable."));
        }
    }
}
