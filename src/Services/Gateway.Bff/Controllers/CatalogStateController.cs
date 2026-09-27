using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/catalog/state")]
public sealed class CatalogStateController : ControllerBase
{
    private readonly IGatewaySessionReader _sessionReader;
    private readonly ICatalogSyncStateClient _catalogSyncClient;
    private readonly IConfiguration _configuration;

    public CatalogStateController(
        IGatewaySessionReader sessionReader,
        ICatalogSyncStateClient catalogSyncClient,
        IConfiguration configuration)
    {
        _sessionReader = sessionReader;
        _catalogSyncClient = catalogSyncClient;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType<CatalogStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        var cookieName = _configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await _sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new { code = "SESSION_UNAUTHORIZED", message = "Session is missing or expired." });

        var correlationId = HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName]?.ToString();
        if (!Guid.TryParse(correlationId, out var parsedCorrelationId))
            parsedCorrelationId = Guid.NewGuid();

        try
        {
            return Ok(await _catalogSyncClient.GetStateAsync(
                session.AccountId,
                parsedCorrelationId.ToString("D"),
                cancellationToken));
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new InternalErrorResponse("CATALOG_SYNC_UNAVAILABLE", "Synchronization service is unavailable."));
        }
    }
}
