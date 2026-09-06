using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Clients;
using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Sync;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/sync")]
public sealed class SyncController(
    IGatewaySessionReader sessionReader,
    ICatalogSyncClient catalogSyncClient,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<SyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateAsync(CancellationToken cancellationToken)
        => await CreateAsync(SyncMode.Full, cancellationToken);

    [HttpPost("incremental")]
    [ProducesResponseType<SyncAccepted>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateIncrementalAsync(CancellationToken cancellationToken)
        => await CreateAsync(SyncMode.Incremental, cancellationToken);

    private async Task<IActionResult> CreateAsync(
        SyncMode mode,
        CancellationToken cancellationToken)
    {
        var cookieName = configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new { code = "SESSION_UNAUTHORIZED", message = "Session is missing or expired." });

        var request = new SyncStartRequest(
            session.AccountId,
            session.EmployeeId,
            mode);

        try
        {
            var accepted = await catalogSyncClient.StartAsync(request, cancellationToken);
            return Accepted(accepted);
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { code = "CATALOG_SYNC_UNAVAILABLE", message = "Synchronization service is unavailable." });
        }
    }
}
