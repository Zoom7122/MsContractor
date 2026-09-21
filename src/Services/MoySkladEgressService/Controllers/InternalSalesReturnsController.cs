using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents/salesreturn")]
public sealed class InternalSalesReturnsController : ControllerBase
{
    private readonly ISalesReturnRecreationOrchestrator _recreationService;
    private readonly IConfiguration _configuration;

    public InternalSalesReturnsController(
        ISalesReturnRecreationOrchestrator recreationService,
        IConfiguration configuration)
    {
        _recreationService = recreationService;
        _configuration = configuration;
    }

    [HttpPost("recreate")]
    [ProducesResponseType<SalesReturnRecreationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<SalesReturnRecreationResult>(StatusCodes.Status207MultiStatus)]
    public async Task<IActionResult> RecreateAsync(
        Guid accountId,
        [FromBody] SalesReturnRecreationRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));
        if (request.MainAgentId == Guid.Empty ||
            request.SalesReturnIds is null ||
            request.SalesReturnIds.Count == 0 ||
            request.SalesReturnIds.Any(id => id == Guid.Empty) ||
            request.SalesReturnIds.Distinct().Count() != request.SalesReturnIds.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_SALESRETURN_RECREATION_REQUEST",
                "A main agent and at least one unique non-empty salesreturn id are required."));
        }

        try
        {
            var result = await _recreationService.RecreateAsync(
                accountId,
                request.MainAgentId,
                request.SalesReturnIds,
                cancellationToken);
            Response.Headers[InternalApiHeaders.CorrelationId] = result.OperationId.ToString("D");
            return result.Documents.All(document => document.Status == "Completed")
                ? Ok(result)
                : StatusCode(StatusCodes.Status207MultiStatus, result);
        }
        catch (EgressException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(new InternalErrorResponse(
                "SALESRETURN_RECREATION_PRECONDITION_FAILED",
                exception.Message));
        }
    }
}
