using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents/salesreturn")]
public sealed class InternalSalesReturnsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly SalesReturnRecreationService _service;

    public InternalSalesReturnsController(
        IConfiguration configuration,
        SalesReturnRecreationService service)
    {
        _configuration = configuration;
        _service = service;
    }

    [HttpPost("recreate")]
    [ProducesResponseType<RecreateSalesReturnsResponse>(200)]
    [ProducesResponseType<RecreateSalesReturnsResponse>(207)]
    public async Task<IActionResult> RecreateAsync(Guid accountId, [FromBody] RecreateSalesReturnsRequest request, CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty || !Header(InternalApiHeaders.UserId, out var user) ||
            !Header(InternalApiHeaders.MergeJobId, out var job) || !Header(InternalApiHeaders.OperationId, out var operation))
            return BadRequest(new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Account, user, merge job and operation ids are required."));
        var correlation = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlation)) correlation = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlation;
        try
        {
            var result = await _service.ExecuteAsync(new SalesReturnCallContext(accountId, user, job, operation, correlation), request, cancellationToken);
            return StatusCode(result.Documents.All(x => x.Status == "Completed") ? 200 : 207, result);
        }
        catch (EgressException exception)
        { return StatusCode(exception.StatusCode, new InternalErrorResponse(exception.Code, exception.SafeMessage)); }
    }
    private bool Header(string name, out Guid value) => Guid.TryParse(Request.Headers[name].ToString(), out value) && value != Guid.Empty;
}
