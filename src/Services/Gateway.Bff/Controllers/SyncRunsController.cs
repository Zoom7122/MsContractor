using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Models;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/sync-runs")]
public sealed class SyncRunsController : ControllerBase
{
    private readonly IGatewaySessionReader _sessionReader;
    private readonly ISyncRunHistoryClient _client;
    private readonly IConfiguration _configuration;

    public SyncRunsController(IGatewaySessionReader sessionReader, ISyncRunHistoryClient client, IConfiguration configuration)
    {
        _sessionReader = sessionReader;
        _client = client;
        _configuration = configuration;
    }

    [HttpGet]
    public async Task<IActionResult> GetAsync([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var session = await ReadSessionAsync(cancellationToken);
        if (session is null) return Unauthorized(SessionError());
        if (!ValidPage(page, pageSize)) return BadRequest(PaginationError());
        try
        {
            return Ok(await _client.GetAsync(session.AccountId, page, pageSize, CorrelationId(), cancellationToken));
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, UnavailableError());
        }
    }

    [HttpGet("{syncRunId:guid}/errors")]
    public async Task<IActionResult> GetErrorsAsync(Guid syncRunId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken cancellationToken = default)
    {
        var session = await ReadSessionAsync(cancellationToken);
        if (session is null) return Unauthorized(SessionError());
        if (syncRunId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_SYNC_RUN_ID", "Sync run ID must not be empty."));
        if (!ValidPage(page, pageSize)) return BadRequest(PaginationError());
        try
        {
            var response = await _client.GetErrorsAsync(session.AccountId, syncRunId, page, pageSize, CorrelationId(), cancellationToken);
            return response is null
                ? NotFound(new InternalErrorResponse("SYNC_RUN_NOT_FOUND", "Sync run was not found."))
                : Ok(response);
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, UnavailableError());
        }
    }

    private Task<GatewaySession?> ReadSessionAsync(CancellationToken cancellationToken)
    {
        Request.Cookies.TryGetValue(_configuration["Session:CookieName"] ?? "mscontractor.session", out var token);
        return _sessionReader.ReadAsync(token, cancellationToken);
    }

    private string CorrelationId()
        => Guid.TryParse(HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName]?.ToString(), out var id)
            ? id.ToString("D") : Guid.NewGuid().ToString("D");

    private static bool ValidPage(int page, int pageSize)
        => page >= 1 && pageSize is >= 1 and <= 100 && ((long)page - 1) * pageSize <= int.MaxValue;

    private static InternalErrorResponse SessionError() => new("SESSION_UNAUTHORIZED", "Session is missing or expired.");
    private static InternalErrorResponse PaginationError() => new("INVALID_PAGINATION", "page must be positive and pageSize between 1 and 100.");
    private static InternalErrorResponse UnavailableError() => new("CATALOG_SYNC_UNAVAILABLE", "Synchronization service is unavailable.");
}
