using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Gateways.Counterparties;
using MsContractor.MoySkladEgressService.Models.Exceptions;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/connection")]
public sealed class InternalConnectionController : ControllerBase
{
    private readonly IMoySkladCounterpartyGateway _gateway;
    private readonly IConfiguration _configuration;

    public InternalConnectionController(
        IMoySkladCounterpartyGateway gateway,
        IConfiguration configuration)
    {
        _gateway = gateway;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType<MoySkladConnectionCheckResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckAsync(
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

        try
        {
            return Ok(await _gateway.CheckConnectionAsync(accountId, correlationId, cancellationToken));
        }
        catch (EgressException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }
}
