using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/sync-runs")]
public sealed class InternalSyncRunsController : ControllerBase
{
    private readonly ISyncRunHistoryService _service;
    private readonly IConfiguration _configuration;

    public InternalSyncRunsController(ISyncRunHistoryService service, IConfiguration configuration)
    {
        _service = service;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetAsync(Guid accountId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var invalid = ValidateRequest(accountId, page, pageSize);
        if (invalid is not null) return invalid;
        return Ok(await _service.GetAsync(accountId, page, pageSize, cancellationToken));
    }

    [HttpGet("{syncRunId:guid}/errors")]
    public async Task<IActionResult> GetErrorsAsync(Guid accountId, Guid syncRunId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var invalid = ValidateRequest(accountId, page, pageSize);
        if (invalid is not null) return invalid;
        if (syncRunId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_SYNC_RUN_ID", "Sync run ID must not be empty."));
        var response = await _service.GetErrorsAsync(accountId, syncRunId, page, pageSize, cancellationToken);
        return response is null
            ? NotFound(new InternalErrorResponse("SYNC_RUN_NOT_FOUND", "Sync run was not found."))
            : Ok(response);
    }

    private IActionResult? ValidateRequest(Guid accountId, int page, int pageSize)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "Account ID must not be empty."));
        if (page < 1 || pageSize is < 1 or > 100 || ((long)page - 1) * pageSize > int.MaxValue)
            return BadRequest(new InternalErrorResponse("INVALID_PAGINATION", "page must be positive and pageSize between 1 and 100."));
        var correlationId = Request.Headers[InternalApiHeaders.CorrelationId].ToString();
        Response.Headers[InternalApiHeaders.CorrelationId] = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.NewGuid().ToString("D") : correlationId;
        return null;
    }
}
