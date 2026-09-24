using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.DuplicatesMergeService.Controllers;

[ApiController]
[Route("internal/merge-preview/selection")]
public sealed class InternalMergeSelectionPreviewController : ControllerBase
{
    private readonly IMergeSelectionPreviewService _previewService;
    private readonly IConfiguration _configuration;

    public InternalMergeSelectionPreviewController(
        IMergeSelectionPreviewService previewService,
        IConfiguration configuration)
    {
        _previewService = previewService;
        _configuration = configuration;
    }

    [HttpPost]
    [ProducesResponseType<MergeSelectionPreviewResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] MergeSelectionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (!Guid.TryParse(Request.Headers[InternalApiHeaders.AccountId], out var accountId) || accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Account header is required."));

        try
        {
            return Ok(await _previewService.GetAsync(accountId, request, cancellationToken));
        }
        catch (MergeSelectionPreviewException exception)
        {
            var error = new InternalErrorResponse(exception.Code, exception.SafeMessage);
            return exception.Error switch
            {
                MergeSelectionPreviewError.NotFound => NotFound(error),
                MergeSelectionPreviewError.Busy => Conflict(error),
                _ => BadRequest(error)
            };
        }
    }
}
