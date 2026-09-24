using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents/factureout")]
public sealed class InternalFactureOutsController : ControllerBase
{
    private readonly IFactureOutRecreationOrchestrator _orchestrator;
    private readonly IConfiguration _configuration;

    public InternalFactureOutsController(
        IFactureOutRecreationOrchestrator orchestrator,
        IConfiguration configuration)
    {
        _orchestrator = orchestrator;
        _configuration = configuration;
    }

    [HttpPost("recreate")]
    [ProducesResponseType<FactureOutRecreationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> RecreateAsync(
        Guid accountId,
        [FromBody] FactureOutRecreationRequest? request,
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
            request.FactureOut is null ||
            request.FactureOut.Count == 0 ||
            request.FactureOut.Count > MaxDocumentCount ||
            request.FactureOut.Any(id => id == Guid.Empty) ||
            request.FactureOut.Distinct().Count() != request.FactureOut.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_FACTUREOUT_RECREATION_REQUEST",
                "A main counterparty and at least one unique non-empty factureout id are required."));
        }

        try
        {
            var result = await _orchestrator.ExecuteAsync(
                accountId,
                request.MainCounterpartyId,
                request.FactureOut,
                cancellationToken);

            var skippedDocuments = result.SkippedDocuments
                .Select(document => new FactureOutSkippedDocumentResponse(
                    document.DocumentId,
                    document.Status,
                    document.ErrorCode,
                    document.Reason ?? document.Exception?.Message))
                .ToArray();

            var response = new FactureOutRecreationResponse(
                result.TransferredDocumentIds.Distinct().ToArray(),
                skippedDocuments
                    .Select(document => document.DocumentId)
                    .Distinct()
                    .ToArray(),
                skippedDocuments,
                result.CreatedWithErrors
                    .Select(document => new FactureOutCreatedWithErrorResponse(
                        document.SourceDocumentId,
                        document.NewDocumentId,
                        document.Status,
                        document.ErrorCode,
                        document.Error))
                    .ToArray(),
                result.FailedDocuments
                    .Select(document => new FactureOutFailedDocumentResponse(
                        document.SourceDocumentId,
                        document.NewSyncId,
                        document.NewDocumentId,
                        document.Status,
                        document.ErrorCode,
                        document.Error))
                    .ToArray());

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
                "FACTUREOUT_PREPARATION_FAILED",
                exception.Message));
        }
    }

    private const int MaxDocumentCount = 100_000;
}
