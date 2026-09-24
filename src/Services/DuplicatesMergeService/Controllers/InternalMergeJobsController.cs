using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.DuplicatesMergeService.Controllers;

[ApiController]
[Route("internal/merge-jobs")]
public sealed class InternalMergeJobsController : ControllerBase
{
    private readonly IMergeJobCreator _creator;
    private readonly IConfiguration _configuration;

    public InternalMergeJobsController(
        IMergeJobCreator creator,
        IConfiguration configuration)
    {
        _creator = creator;
        _configuration = configuration;
    }

    [HttpPost]
    [ProducesResponseType<MergeJobAccepted>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreateMergeJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (!TryGuidHeader(InternalApiHeaders.AccountId, out var accountId) ||
            !TryGuidHeader(InternalApiHeaders.UserId, out var userId) ||
            !TryGuidHeader(InternalApiHeaders.CorrelationId, out var correlationId))
        {
            return BadRequest(new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Account, user, and correlation headers are required."));
        }

        try
        {
            return Accepted(await _creator.CreateAsync(
                accountId, userId, correlationId, request, cancellationToken));
        }
        catch (MergeRequestException exception)
        {
            var error = new InternalErrorResponse(exception.Code, exception.SafeMessage);
            return exception.Error switch
            {
                MergeRequestError.NotFound => NotFound(error),
                MergeRequestError.MainArchived => Conflict(error),
                MergeRequestError.CounterpartyBusy => Conflict(error),
                _ => BadRequest(error)
            };
        }
    }

    private bool TryGuidHeader(string name, out Guid value) =>
        Guid.TryParse(Request.Headers[name].ToString(), out value) && value != Guid.Empty;
}
