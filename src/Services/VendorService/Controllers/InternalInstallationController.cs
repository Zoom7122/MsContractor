using MsContractor.VendorService.Repositories;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using MsContractor.BuildingBlocks.Security;
using MsContractor.Contracts.Internal;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Controllers;

[ApiController]
[Route("internal/vendor/installations")]
public sealed class InternalInstallationController : ControllerBase
{
    private readonly IVendorInstallationRepository _repository;
    private readonly AccessTokenProtector _tokenProtector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InternalInstallationController> _logger;

    public InternalInstallationController(
        IVendorInstallationRepository repository,
        AccessTokenProtector tokenProtector,
        IConfiguration configuration,
        ILogger<InternalInstallationController> logger)
    {
        _repository = repository;
        _tokenProtector = tokenProtector;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet("{accountId:guid}/token")]
    [ProducesResponseType<InternalAccessTokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<InternalErrorResponse>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTokenAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        if (!InternalApiKeyAuthentication.IsAuthorized(Request, _configuration))
            return Unauthorized(new InternalErrorResponse("INTERNAL_UNAUTHORIZED", "Internal authentication failed."));

        var installation = await _repository.GetByAccountIdAsync(accountId, cancellationToken);
        if (installation is null)
            return NotFound(new InternalErrorResponse("INSTALLATION_NOT_FOUND", "Installation was not found."));
        if (!string.Equals(installation.Status, "Active", StringComparison.Ordinal))
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new InternalErrorResponse("INSTALLATION_INACTIVE", "Installation is inactive."));
        if (installation.AccessTokenCiphertext is null ||
            installation.AccessTokenNonce is null ||
            installation.AccessTokenTag is null ||
            installation.TokenKeyVersion is null)
        {
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new InternalErrorResponse("ACCESS_TOKEN_UNAVAILABLE", "Access token is unavailable."));
        }

        try
        {
            var accessToken = _tokenProtector.Unprotect(
                installation.AccessTokenCiphertext,
                installation.AccessTokenNonce,
                installation.AccessTokenTag,
                installation.TokenKeyVersion.Value);
            return Ok(new InternalAccessTokenResponse(accessToken));
        }
        catch (CryptographicException exception)
        {
            _logger.LogError(
                exception,
                "Could not decrypt access token for account {AccountId}.",
                accountId);
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new InternalErrorResponse("ACCESS_TOKEN_UNAVAILABLE", "Access token is unavailable."));
        }
    }
}
