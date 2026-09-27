using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.CatalogSyncService.Controllers;

[ApiController]
[Route("internal/accounts/{accountId:guid}/settings")]
public sealed class InternalSettingsController : ControllerBase
{
    private readonly ICatalogSettingsService _settingsService;
    private readonly IConfiguration _configuration;

    public InternalSettingsController(
        ICatalogSettingsService settingsService,
        IConfiguration configuration)
    {
        _settingsService = settingsService;
        _configuration = configuration;
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SaveAsync(
        Guid accountId,
        [FromBody] CatalogSettingsRequest? request,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));
        if (accountId == Guid.Empty)
            return BadRequest(new InternalErrorResponse("INVALID_ACCOUNT_ID", "accountId must be a non-empty guid."));

        try
        {
            await _settingsService.SaveAsync(accountId, request, cancellationToken);
            return NoContent();
        }
        catch (CatalogSettingsValidationException exception)
        {
            return BadRequest(new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }
}
