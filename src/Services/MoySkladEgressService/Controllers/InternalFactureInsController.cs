using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Services.Documents.Facturein;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents/facturein")]
public sealed class InternalFactureInsController : ControllerBase
{
    private readonly IFactureInRecreationOrchestrator _orchestrator;
    private readonly IConfiguration _configuration;

    public InternalFactureInsController(
        IFactureInRecreationOrchestrator orchestrator,
        IConfiguration configuration)
    {
        _orchestrator = orchestrator;
        _configuration = configuration;
    }

    [HttpPost("recreate")]
    [ProducesResponseType<FactureInRecreationResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status501NotImplemented)]
    public async Task<IActionResult> RecreateAsync(
        Guid accountId,
        [FromBody] FactureInRecreationRequest? request,
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
            request.FactureInIds is null ||
            request.FactureInIds.Count == 0 ||
            request.FactureInIds.Count > MaxDocumentCount ||
            request.FactureInIds.Any(id => id == Guid.Empty) ||
            request.FactureInIds.Distinct().Count() != request.FactureInIds.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_FACTUREIN_RECREATION_REQUEST",
                "A main counterparty and at least one unique non-empty facturein id are required."));
        }

        try
        {
            var result = await _orchestrator.ExecuteAsync(
                accountId,
                request.MainCounterpartyId,
                request.FactureInIds,
                cancellationToken);

            var skippedDocuments = result.SkippedDocuments
                .Select(document => new FactureInSkippedDocumentResponse(
                    document.DocumentId,
                    document.Status,
                    document.ErrorCode,
                    document.Reason ?? document.Exception?.Message))
                .ToArray();

            var response = new FactureInRecreationResponse(
                result.TransferredDocumentIds.Distinct().ToArray(),
                skippedDocuments
                    .Select(document => document.DocumentId)
                    .Distinct()
                    .ToArray(),
                skippedDocuments,
                result.CreatedWithErrors
                    .Select(document => new FactureInCreatedWithErrorResponse(
                        document.SourceDocumentId,
                        document.NewDocumentId,
                        document.Status,
                        document.ErrorCode,
                        document.Error))
                    .ToArray(),
                result.FailedDocuments
                    .Select(document => new FactureInFailedDocumentResponse(
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
                "FACTUREIN_PREPARATION_FAILED",
                exception.Message));
        }
    }

    private const int MaxDocumentCount = 100_000;
}
