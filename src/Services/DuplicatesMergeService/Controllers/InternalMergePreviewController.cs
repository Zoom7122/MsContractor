using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Duplicates;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.DuplicatesMergeService.Controllers;

[ApiController]
[Route("internal/merge-preview")]
public sealed class InternalMergePreviewController(
    IDuplicatePreviewService previewService,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromQuery(Name = "fields")] string[]? fields,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, configuration))
            return Unauthorized(new { code = "INTERNAL_UNAUTHORIZED", message = "Internal authentication failed." });
        if (!Guid.TryParse(Request.Headers[InternalApiHeaders.AccountId], out var accountId) || accountId == Guid.Empty)
            return BadRequest(new { code = "INVALID_INTERNAL_CONTEXT", message = "Account header is required." });
        if (!DuplicateMatchFields.TryParse(fields, out var selectedFields))
            return BadRequest(new { code = "INVALID_DUPLICATE_FIELDS", message = "fields must contain name, email, or phone." });

        var result = await previewService.FindAsync(accountId, selectedFields, cancellationToken);
        return Ok(result);
    }
}
