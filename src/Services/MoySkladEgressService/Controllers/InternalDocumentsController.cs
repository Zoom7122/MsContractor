using MsContractor.MoySkladEgressService.Models.Exceptions;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Services.Documents;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/documents")]
public sealed class InternalDocumentsController : ControllerBase
{
    private readonly IMoySkladDocumentDiscoveryService _discoveryService;
    private readonly IMoySkladDocumentChangeService _changeService;
    private readonly IMoySkladDocumentAgentAndContractService _agentAndContractService;
    private readonly IMoySkladMergeVerificationSnapshotService _mergeVerificationSnapshotService;
    private readonly IConfiguration _configuration;

    public InternalDocumentsController(
        IMoySkladDocumentDiscoveryService discoveryService,
        IMoySkladDocumentChangeService changeService,
        IMoySkladDocumentAgentAndContractService agentAndContractService,
        IMoySkladMergeVerificationSnapshotService mergeVerificationSnapshotService,
        IConfiguration configuration)
    {
        _discoveryService = discoveryService;
        _changeService = changeService;
        _agentAndContractService = agentAndContractService;
        _mergeVerificationSnapshotService = mergeVerificationSnapshotService;
        _configuration = configuration;
    }

    [HttpPost("merge-verification-snapshot")]
    [ProducesResponseType<MoySkladMergeVerificationSnapshotResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CaptureMergeVerificationSnapshotAsync(
        Guid accountId,
        [FromBody] MoySkladMergeVerificationSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty || request.CounterpartyIds is null || request.CounterpartyIds.Count == 0 ||
            request.CounterpartyIds.Any(id => id == Guid.Empty) ||
            request.CounterpartyIds.Distinct().Count() != request.CounterpartyIds.Count)
        {
            return BadRequest(new InternalErrorResponse("INVALID_COUNTERPARTY_IDS",
                "At least one unique non-empty counterparty id is required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;
        try
        {
            return Ok(await _mergeVerificationSnapshotService.CaptureAsync(accountId, request.CounterpartyIds,
                correlationId, cancellationToken));
        }
        catch (EgressException exception)
        {
            return StatusCode(exception.StatusCode, new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }

    [HttpPost("discover")]
    [ProducesResponseType<MoySkladDocumentDiscoveryResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> DiscoverAsync(
        Guid accountId,
        [FromBody] MoySkladDocumentDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));
        if (request.CounterpartyIds is null ||
            request.CounterpartyIds.Count == 0 ||
            request.CounterpartyIds.Any(id => id == Guid.Empty) ||
            request.CounterpartyIds.Distinct().Count() != request.CounterpartyIds.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_COUNTERPARTY_IDS",
                "At least one unique non-empty counterparty id is required."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.UserId, out var userId))
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_INTERNAL_CONTEXT",
                "A valid user header is required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        try
        {
            var result = await _discoveryService.DiscoverAsync(
                accountId,
                userId,
                correlationId,
                request.CounterpartyIds,
                cancellationToken);
            return Ok(result);
        }
        catch (EgressException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }

    [HttpPost("change-counterparty")]
    [ProducesResponseType<MoySkladDocumentChangeCounterpartyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MoySkladDocumentChangeCounterpartyResponse>(StatusCodes.Status207MultiStatus)]
    public async Task<IActionResult> ChangeCounterpartyAsync(
        Guid accountId,
        [FromBody] MoySkladDocumentChangeCounterpartyRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));
        if (request.MainCounterpartyId == Guid.Empty || request.Documents is null || request.Documents.Count == 0 ||
            request.Documents.Any(item => string.IsNullOrWhiteSpace(item.DocumentType) || item.DocumentId == Guid.Empty) ||
            request.Documents.Select(item => (item.DocumentType, item.DocumentId)).Distinct().Count() != request.Documents.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_DOCUMENT_CHANGE_REQUEST",
                "A main counterparty and at least one unique document type/id pair are required."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.UserId, out var userId) ||
            !TryHeaderGuid(InternalApiHeaders.MergeJobId, out var mergeJobId) ||
            !TryHeaderGuid(InternalApiHeaders.OperationId, out var operationId))
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_INTERNAL_CONTEXT",
                "Merge job, operation, and user headers are required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        var result = await _changeService.ChangeCounterpartyAsync(
            accountId,
            userId,
            mergeJobId,
            operationId,
            correlationId,
            request,
            cancellationToken);
        return result.FailedCount == 0
            ? Ok(result)
            : StatusCode(StatusCodes.Status207MultiStatus, result);
    }

    [HttpPost("change-agent-and-contract")]
    [ProducesResponseType<MoySkladDocumentChangeCounterpartyResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<MoySkladDocumentChangeCounterpartyResponse>(StatusCodes.Status207MultiStatus)]
    public async Task<IActionResult> ChangeAgentAndContractAsync(
        Guid accountId,
        [FromBody] MoySkladDocumentChangeAgentAndContractRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));
        if (request.MainCounterpartyId == Guid.Empty || request.Documents is null || request.Documents.Count == 0 ||
            request.Documents.Any(item => string.IsNullOrWhiteSpace(item.DocumentType) || item.DocumentId == Guid.Empty ||
                                          item.Contract == Guid.Empty) ||
            request.Documents.Select(item => (item.DocumentType, item.DocumentId)).Distinct().Count() != request.Documents.Count)
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_DOCUMENT_CHANGE_REQUEST",
                "A main counterparty and at least one unique document type/id pair are required."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.UserId, out var userId) ||
            !TryHeaderGuid(InternalApiHeaders.MergeJobId, out var mergeJobId) ||
            !TryHeaderGuid(InternalApiHeaders.OperationId, out var operationId))
        {
            return BadRequest(new InternalErrorResponse(
                "INVALID_INTERNAL_CONTEXT",
                "Merge job, operation, and user headers are required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");
        Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        var result = await _agentAndContractService.ChangeAgentAndContractAsync(
            accountId, userId, mergeJobId, operationId, correlationId, request, cancellationToken);
        return result.FailedCount == 0
            ? Ok(result)
            : StatusCode(StatusCodes.Status207MultiStatus, result);
    }

    private bool TryHeaderGuid(string name, out Guid value) =>
        Guid.TryParse(Request.Headers[name].ToString(), out value) && value != Guid.Empty;

}
