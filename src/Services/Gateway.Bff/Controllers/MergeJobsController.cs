using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Clients;
using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/merge-jobs")]
public sealed class MergeJobsController : ControllerBase
{
    private readonly IGatewaySessionReader _sessionReader;
    private readonly IMergeJobsClient _mergeJobsClient;
    private readonly IConfiguration _configuration;

    public MergeJobsController(
        IGatewaySessionReader sessionReader,
        IMergeJobsClient mergeJobsClient,
        IConfiguration configuration)
    {
        _sessionReader = sessionReader;
        _mergeJobsClient = mergeJobsClient;
        _configuration = configuration;
    }

    [HttpPost]
    [ProducesResponseType<MergeJobAccepted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateMergeJobRequest request,
        CancellationToken cancellationToken)
    {
        var cookieName = _configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await _sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new { code = "SESSION_UNAUTHORIZED", message = "Session is missing or expired." });

        var correlationText = HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName]?.ToString();
        var correlationId = Guid.TryParse(correlationText, out var parsed) ? parsed : Guid.NewGuid();
        try
        {
            return Accepted(await _mergeJobsClient.CreateAsync(
                session.AccountId,
                session.EmployeeId,
                correlationId,
                request,
                cancellationToken));
        }
        catch (MergeJobsClientException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }
}
