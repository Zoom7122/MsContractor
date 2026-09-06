using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Clients;
using Microsoft.AspNetCore.Mvc;
using MsContractor.Contracts.Internal;
using MsContractor.Contracts.Merge;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/merge-preview/selection")]
public sealed class MergeSelectionPreviewController(
    IGatewaySessionReader sessionReader,
    IMergeSelectionPreviewClient previewClient,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<MergeSelectionPreviewResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateAsync(
        [FromBody] MergeSelectionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var cookieName = configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new InternalErrorResponse("SESSION_UNAUTHORIZED", "Session is missing or expired."));

        try
        {
            return Ok(await previewClient.GetAsync(session.AccountId, request, cancellationToken));
        }
        catch (MergeSelectionPreviewClientException exception)
        {
            return StatusCode(
                exception.StatusCode,
                new InternalErrorResponse(exception.Code, exception.SafeMessage));
        }
    }
}
