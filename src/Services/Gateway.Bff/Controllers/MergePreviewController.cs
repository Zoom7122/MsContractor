using Microsoft.AspNetCore.Mvc;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Gateway.Bff.Controllers;

[ApiController]
[Route("api/merge-preview")]
public sealed class MergePreviewController(
    IGatewaySessionReader sessionReader,
    IDuplicatePreviewClient previewClient,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromQuery(Name = "fields")] string[]? fields,
        CancellationToken cancellationToken)
    {
        var cookieName = configuration["Session:CookieName"] ?? "mscontractor.session";
        Request.Cookies.TryGetValue(cookieName, out var token);
        var session = await sessionReader.ReadAsync(token, cancellationToken);
        if (session is null)
            return Unauthorized(new { code = "SESSION_UNAUTHORIZED", message = "Session is missing or expired." });

        try
        {
            return Ok(await previewClient.FindAsync(session.AccountId, fields ?? [], cancellationToken));
        }
        catch (DuplicatePreviewValidationException)
        {
            return BadRequest(new { code = "INVALID_DUPLICATE_FIELDS", message = "fields must contain name, email, or phone." });
        }
        catch (DuplicatePreviewUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { code = "DUPLICATE_PREVIEW_UNAVAILABLE", message = "Duplicate preview service is unavailable." });
        }
    }
}
