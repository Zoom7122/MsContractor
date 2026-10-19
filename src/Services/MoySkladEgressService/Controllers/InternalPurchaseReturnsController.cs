using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents/purchasereturn")]
public sealed class InternalPurchaseReturnsController : ControllerBase
{
    private readonly IPurchaseReturnRecreationOrchestrator _orchestrator;
    private readonly IConfiguration _configuration;

    public InternalPurchaseReturnsController(
        IPurchaseReturnRecreationOrchestrator orchestrator,
        IConfiguration configuration)
    {
        _orchestrator = orchestrator;
        _configuration = configuration;
    }

    [HttpPost("recreate")]
    [ProducesResponseType<PurchaseReturnRecreationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecreateAsync(
        Guid accountId,
        [FromBody] PurchaseReturnRecreationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse(
                "INTERNAL_UNAUTHORIZED",
                "Internal authentication failed."));

        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse(
                "INVALID_ACCOUNT_ID",
                "accountId must be a non-empty guid."));

        if (request is null ||
            request.MainCounterpartyId == Guid.Empty ||
            request.PurchaseReturnIds is null ||
            request.PurchaseReturnIds.Count == 0 ||
            request.PurchaseReturnIds.Count > MaxDocumentCount ||
            request.PurchaseReturnIds.Any(id => id == Guid.Empty) ||
            request.PurchaseReturnIds.Distinct().Count() != request.PurchaseReturnIds.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_PURCHASERETURN_RECREATION_REQUEST",
                "A main counterparty and at least one unique non-empty purchasereturn id are required."));
        }

        try
        {
            var result = await _orchestrator.ExecuteAsync(
                accountId,
                request.MainCounterpartyId,
                request.PurchaseReturnIds,
                cancellationToken);
            var unCreatedButDeletedIds = result.UnCreatedButDeleted
                .Select(document => document.SourceDocumentId)
                .ToHashSet();
            var transferredDocumentIds = result.Documents
                .Where(IsSuccessfullyCreated)
                .Select(document => document.SourceDocumentId)
                .Distinct()
                .ToArray();
            var response = new PurchaseReturnRecreationResponse(
                transferredDocumentIds,
                result.Documents
                    .Where(document => document.NewDocumentId is null &&
                                       !unCreatedButDeletedIds.Contains(document.SourceDocumentId))
                    .Select(document => document.SourceDocumentId)
                    .Distinct()
                    .ToArray(),
                result.Documents
                    .Where(document => document.NewDocumentId is null &&
                                       !unCreatedButDeletedIds.Contains(document.SourceDocumentId))
                    .Select(document => new PurchaseReturnSkippedDocumentResponse(
                        document.SourceDocumentId,
                        document.Status,
                        document.ErrorCode,
                        document.Error))
                    .ToArray(),
                result.Documents
                    .Where(document => document.NewDocumentId is not null && !IsSuccessfullyCreated(document))
                    .Select(document => new PurchaseReturnCreatedWithErrorResponse(
                        document.SourceDocumentId,
                        document.NewDocumentId!.Value,
                        document.Status,
                        document.ErrorCode,
                        document.Error))
                    .ToArray())
            {
                TransferredDocuments = result.Documents
                    .Where(IsSuccessfullyCreated)
                    .Select(item => new DocumentRecreationTransferredDocumentResponse(
                        item.SourceDocumentId,
                        item.NewDocumentId!.Value))
                    .ToArray(),
                UnCreatedButDeleted = result.UnCreatedButDeleted
                    .Select(item => new DocumentRecreationUnCreatedButDeletedResponse(
                        item.SourceDocumentId,
                        item.Status,
                        item.ErrorCode,
                        item.Error))
                    .ToArray(),
                RecreatedPurchaseReturns = result.RecreatedPurchaseReturns.Select(item =>
                    new RecreatedPurchaseReturnResponse(item.SourceDocumentId, item.NewDocumentId)).ToArray(),
                RecreatedFactureOuts = result.RecreatedFactureOuts.Select(item =>
                    new RecreatedPurchaseReturnFactureOutResponse(
                        item.SourcePurchaseReturnId, item.SourceDocumentId, item.NewDocumentId)).ToArray()
            };
            return Accepted(response);
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
                "PURCHASERETURN_PREPARATION_FAILED",
                exception.Message));
        }
    }

    private static bool IsSuccessfullyCreated(PurchaseReturnDocumentVerificationResult document) =>
        document.NewDocumentId is not null &&
        document.Status is "Recreated" or "Verified" or "NeedsManualReview";

    private const int MaxDocumentCount = 100_000;
}
