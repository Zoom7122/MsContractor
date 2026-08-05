using System.Text;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/counterparties")]
public sealed class InternalCounterpartiesController(
    IMoySkladCounterpartyGateway gateway,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAsync(
        Guid accountId,
        [FromQuery] bool archived = false,
        [FromQuery] int limit = 1000,
        [FromQuery] int offset = 0,
        [FromQuery] DateTimeOffset? windowFrom = null,
        [FromQuery] DateTimeOffset? windowTo = null,
        CancellationToken cancellationToken = default)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (limit is < 1 or > 1000)
            return BadRequest(new InternalErrorResponse("INVALID_LIMIT", "limit must be between 1 and 1000."));
        if (offset < 0)
            return BadRequest(new InternalErrorResponse("INVALID_OFFSET", "offset must be greater than or equal to 0."));
        if ((windowFrom is null) != (windowTo is null) || windowFrom >= windowTo)
        {
            return BadRequest(
                new InternalErrorResponse(
                    "INVALID_SYNC_WINDOW",
                    "windowFrom and windowTo must be provided together and windowFrom must be earlier than windowTo."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.SyncRunId, out var syncRunId) ||
            !TryHeaderGuid(InternalApiHeaders.UserId, out var userId))
        {
            return BadRequest(
                new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Sync and user headers are required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");

        try
        {
            var result = await gateway.GetAsync(
                accountId,
                archived,
                limit,
                offset,
                windowFrom,
                windowTo,
                syncRunId,
                userId,
                correlationId,
                cancellationToken);
            foreach (var header in result.SafeHeaders)
                Response.Headers[header.Key] = header.Value;
            Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;
            return Content(result.Json, "application/json", Encoding.UTF8);
        }
        catch (EgressException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }

    private bool TryHeaderGuid(string name, out Guid value) =>
        Guid.TryParse(Request.Headers[name].ToString(), out value) && value != Guid.Empty;
}
