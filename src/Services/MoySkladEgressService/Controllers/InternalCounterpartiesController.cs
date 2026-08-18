using System.Text;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.MoySkladEgressService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/counterparties")]
public sealed class InternalCounterpartiesController(
    IMoySkladCounterpartyGateway gateway,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAsync(
        Guid accountId,
        [FromQuery] bool archived = false,
        [FromQuery] int limit = 1000,
        [FromQuery] int offset = 0,
        [FromQuery] DateTimeOffset? windowFrom = null,
        [FromQuery] DateTimeOffset? windowTo = null,
        CancellationToken cancellationToken = default)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (limit is < 1 or > 1000)
            return BadRequest(new InternalErrorResponse("INVALID_LIMIT", "limit must be between 1 and 1000."));
        if (offset < 0)
            return BadRequest(new InternalErrorResponse("INVALID_OFFSET", "offset must be greater than or equal to 0."));
        if ((windowFrom is null) != (windowTo is null) || windowFrom >= windowTo)
        {
            return BadRequest(
                new InternalErrorResponse(
                    "INVALID_SYNC_WINDOW",
                    "windowFrom and windowTo must be provided together and windowFrom must be earlier than windowTo."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.SyncRunId, out var syncRunId) ||
            !TryHeaderGuid(InternalApiHeaders.UserId, out var userId))
        {
            return BadRequest(
                new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Sync and user headers are required."));
        }

        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("D");

        try
        {
            var result = await gateway.GetAsync(
                accountId,
                archived,
                limit,
                offset,
                windowFrom,
                windowTo,
                syncRunId,
                userId,
                correlationId,
                cancellationToken);
            foreach (var header in result.SafeHeaders)
                Response.Headers[header.Key] = header.Value;
            Response.Headers[InternalApiHeaders.CorrelationId] = correlationId;
            return Content(result.Json, "application/json", Encoding.UTF8);
        }
        catch (EgressException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }

    [HttpPut("{counterpartyId:guid}")]
    public async Task<IActionResult> UpdateAsync(
        Guid accountId,
        Guid counterpartyId,
        [FromBody] InternalCounterpartyUpdateRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (counterpartyId == Guid.Empty || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new InternalErrorResponse("INVALID_COUNTERPARTY_UPDATE", "Counterparty update is invalid."));
        if (!TryOperationContext(out var mergeJobId, out var operationId, out var userId))
            return BadRequest(new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Merge job, operation, and user headers are required."));

        return await ExecutePutAsync(
            () => gateway.UpdateAsync(
                accountId, counterpartyId, request, mergeJobId, operationId, userId,
                CorrelationId(), cancellationToken),
            cancellationToken);
    }

    [HttpPut("archive")]
    public async Task<IActionResult> ArchiveAsync(
        Guid accountId,
        [FromBody] InternalCounterpartyBatchArchiveRequest request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (request.CounterpartyIds is null || request.CounterpartyIds.Count == 0 ||
            request.CounterpartyIds.Any(id => id == Guid.Empty) ||
            request.CounterpartyIds.Distinct().Count() != request.CounterpartyIds.Count)
        {
            return BadRequest(new InternalErrorResponse("INVALID_COUNTERPARTY_IDS", "At least one unique counterparty id is required."));
        }
        if (!TryHeaderGuid(InternalApiHeaders.MergeJobId, out var mergeJobId) ||
            !TryHeaderGuid(InternalApiHeaders.UserId, out var userId))
        {
            return BadRequest(new InternalErrorResponse("INVALID_INTERNAL_CONTEXT", "Merge job and user headers are required."));
        }

        return await ExecutePutAsync(
            () => gateway.ArchiveAsync(
                accountId, request.CounterpartyIds, mergeJobId, userId,
                CorrelationId(), cancellationToken),
            cancellationToken);
    }

    private async Task<IActionResult> ExecutePutAsync(
        Func<Task<MoySkladRawResponse>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await action();
            Response.Headers[InternalApiHeaders.CorrelationId] = CorrelationId();
            return Content(result.Json, "application/json", Encoding.UTF8);
        }
        catch (EgressException exception)
        {
            return StatusCode(exception.StatusCode, new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }

    private bool TryOperationContext(out Guid mergeJobId, out Guid operationId, out Guid userId)
    {
        var hasMergeJob = TryHeaderGuid(InternalApiHeaders.MergeJobId, out mergeJobId);
        var hasOperation = TryHeaderGuid(InternalApiHeaders.OperationId, out operationId);
        var hasUser = TryHeaderGuid(InternalApiHeaders.UserId, out userId);
        return hasMergeJob && hasOperation && hasUser;
    }

    private string CorrelationId()
    {
        var value = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        return string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("D") : value;
    }

    private bool TryHeaderGuid(string name, out Guid value) =>
        Guid.TryParse(Request.Headers[name].ToString(), out value) && value != Guid.Empty;
}
