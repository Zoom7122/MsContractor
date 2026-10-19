using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/merge-jobs/{mergeJobId:guid}/documents-in-operation")]
public sealed class InternalDocumentsInOperationController : ControllerBase
{
    private readonly IDocumentsInOperationStateService _documentsInOperationService;
    private readonly IConfiguration _configuration;

    public InternalDocumentsInOperationController(
        IDocumentsInOperationStateService documentsInOperationService,
        IConfiguration configuration)
    {
        _documentsInOperationService = documentsInOperationService;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DocumentsInOperationContent>>(StatusCodes.Status200OK)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetAsync(
        Guid accountId,
        Guid mergeJobId,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));
        if (mergeJobId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_MERGE_JOB_ID", "mergeJobId must be a non-empty guid."));

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        var documents = await _documentsInOperationService.GetForMergeJobAsync(
            accountId,
            mergeJobId,
            cancellationToken);
        return Ok(documents);
    }
}
