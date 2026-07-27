using Microsoft.AspNetCore.Mvc;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Services;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Controllers;

[ApiController]
[Route("api/moysklad/vendor/1.0/apps")]
public sealed class VendorInstallationController(VendorInstallationService service) : ControllerBase
{
    [HttpPut("{appId:guid}/{accountId:guid}")]
    [ProducesResponseType<VendorActivationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ActivateAsync(
        Guid appId,
        Guid accountId,
        [FromBody] VendorActivationRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "VENDOR_VALIDATION_ERROR", "A request body is required.");

        var requestId = Request.Headers["X-Lognex-RequestId"].FirstOrDefault()
            ?? Request.Headers["X_Lognex_RequestId"].FirstOrDefault()
            ?? string.Empty;
        var authorization = Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;

        try
        {
            await service.ActivateAsync(new VendorActivationCommand(
                appId, accountId, authorization, requestId, request), cancellationToken);
            return Ok(new VendorActivationResponse("Activated"));
        }
        catch (VendorAuthenticationException)
        {
            return Error(StatusCodes.Status401Unauthorized, "VENDOR_AUTHENTICATION_ERROR", "Authentication failed.");
        }
        catch (VendorForbiddenException)
        {
            return Error(StatusCodes.Status403Forbidden, "VENDOR_FORBIDDEN", "Application is not authorized.");
        }
        catch (VendorNotFoundException)
        {
            return Error(StatusCodes.Status404NotFound, "VENDOR_NOT_FOUND", "Installation was not found.");
        }
        catch (VendorConflictException)
        {
            return Error(StatusCodes.Status409Conflict, "VENDOR_CONFLICT", "Request conflicts with existing data.");
        }
        catch (VendorValidationException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "VENDOR_VALIDATION_ERROR", exception.Message);
        }
    }

    [HttpDelete("{appId:guid}/{accountId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<VendorErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeactivateAsync(
        Guid appId,
        Guid accountId,
        [FromBody] VendorDeactivationRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return Error(StatusCodes.Status400BadRequest, "VENDOR_VALIDATION_ERROR", "A request body is required.");

        var requestId = Request.Headers["X-Lognex-RequestId"].FirstOrDefault()
            ?? Request.Headers["X_Lognex_RequestId"].FirstOrDefault()
            ?? string.Empty;
        var authorization = Request.Headers.Authorization.FirstOrDefault() ?? string.Empty;

        try
        {
            await service.DeactivateAsync(new VendorDeactivationCommand(
                appId, accountId, authorization, requestId, request), cancellationToken);
            return Ok();
        }
        catch (VendorAuthenticationException)
        {
            return Error(StatusCodes.Status401Unauthorized, "VENDOR_AUTHENTICATION_ERROR", "Authentication failed.");
        }
        catch (VendorForbiddenException)
        {
            return Error(StatusCodes.Status403Forbidden, "VENDOR_FORBIDDEN", "Application is not authorized.");
        }
        catch (VendorConflictException)
        {
            return Error(StatusCodes.Status409Conflict, "VENDOR_CONFLICT", "Request conflicts with existing data.");
        }
        catch (VendorValidationException exception)
        {
            return Error(StatusCodes.Status400BadRequest, "VENDOR_VALIDATION_ERROR", exception.Message);
        }
    }

    private ObjectResult Error(int statusCode, string code, string message) =>
        StatusCode(statusCode, new VendorErrorResponse(code, message));
}
