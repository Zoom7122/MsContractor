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
    [ProducesResponseType<SalesReturnRecreationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<SalesReturnRecreationResponse>(StatusCodes.Status207MultiStatus)]
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
            var allDocumentsTransferred = result.TransferredDocumentIds.Count == request.SalesReturnIds.Count &&
                                          result.TransferredDocumentIds.ToHashSet().SetEquals(request.SalesReturnIds) &&
                                          result.TransferredDocuments.Count == request.SalesReturnIds.Count &&
                                          result.TransferredDocuments.Select(item => item.SourceDocumentId)
                                              .ToHashSet().SetEquals(request.SalesReturnIds);
            var hasIncompleteOutcome = result.SkippedDocumentIds.Count != 0 ||
                                       result.SkippedDocuments.Count != 0 ||
                                       result.CreatedWithErrors.Count != 0 ||
                                       result.UnCreatedButDeleted.Count != 0;
            var response = new SalesReturnRecreationResponse(
                result.TransferredDocumentIds,
                result.TransferredDocuments.Select(item => new SalesReturnTransferredDocumentResponse(
                    item.SourceDocumentId,
                    item.NewDocumentId)).ToArray(),
                result.SkippedDocumentIds,
                result.SkippedDocuments.Select(item => new SalesReturnSkippedDocumentResponse(
                    item.DocumentId,
                    item.Status,
                    item.ErrorCode,
                    item.Error)).ToArray(),
                result.CreatedWithErrors.Select(item => new SalesReturnCreatedWithErrorResponse(
                    item.SourceDocumentId,
                    item.NewDocumentId,
                    item.Status,
                    item.ErrorCode,
                    item.Error)).ToArray(),
                result.UnCreatedButDeleted.Select(item => new SalesReturnUnCreatedButDeletedResponse(
                    item.SourceDocumentId,
                    item.Status,
                    item.ErrorCode,
                    item.Error)).ToArray());
            return allDocumentsTransferred && !hasIncompleteOutcome
                ? Ok(response)
                : StatusCode(StatusCodes.Status207MultiStatus, response);
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
