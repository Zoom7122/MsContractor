using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/state")]
public sealed class InternalStateController : ControllerBase
{
    private readonly IStatePreparationService _statePreparationService;
    private readonly IConfiguration _configuration;

    public InternalStateController(
        IStatePreparationService statePreparationService,
        IConfiguration configuration)
    {
        _statePreparationService = statePreparationService;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType<CatalogStateResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        var response = await _statePreparationService.PrepareAsync(
            accountId,
            correlationId,
            cancellationToken);
        return Ok(response);
    }
}
