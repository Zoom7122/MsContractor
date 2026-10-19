using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/catalog/merge-jobs/{mergeJobId:guid}/documents-in-operation")]
public sealed class CatalogMergeJobDocumentsController : ControllerBase
{
    private readonly IGatewaySessionReader _sessionReader;
    private readonly ICatalogSyncStateClient _catalogSyncClient;
    private readonly IConfiguration _configuration;

    public CatalogMergeJobDocumentsController(
        IGatewaySessionReader sessionReader,
        ICatalogSyncStateClient catalogSyncClient,
        IConfiguration configuration)
    {
        _sessionReader = sessionReader;
        _catalogSyncClient = catalogSyncClient;
        _configuration = configuration;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<DocumentsInOperationContent>>(StatusCodes.Status200OK)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetAsync(Guid mergeJobId, CancellationToken cancellationToken)
    {
        if (mergeJobId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_MERGE_JOB_ID", "mergeJobId must be a non-empty guid."));

        var cookieName = _configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await _sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new InternalErrorResponse("SESSION_UNAUTHORIZED", "Session is missing or expired."));

        var correlationId = HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName]?.ToString();
        if (!Guid.TryParse(correlationId, out var parsedCorrelationId))
            parsedCorrelationId = Guid.NewGuid();

        try
        {
            var documents = await _catalogSyncClient.GetDocumentsInOperationAsync(
                session.AccountId,
                mergeJobId,
                parsedCorrelationId.ToString("D"),
                cancellationToken);
            return Ok(documents);
        }
        catch (CatalogSyncUnavailableException)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new InternalErrorResponse("CATALOG_SYNC_UNAVAILABLE", "Synchronization service is unavailable."));
        }
    }
}
