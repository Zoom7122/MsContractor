using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Services;

public sealed class VendorOptions
{
    public const string SectionName = "Vendor";
    public Guid AppId { get; init; }
    public string AppUid { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string AccessTokenEncryptionKey { get; init; } = string.Empty;
    public int TokenKeyVersion { get; init; }
}

public sealed class VendorOptionsValidator : IValidateOptions<VendorOptions>
{
    public ValidateOptionsResult Validate(string? name, VendorOptions options)
    {
        if (options.AppId == Guid.Empty || string.IsNullOrWhiteSpace(options.AppUid) ||
            string.IsNullOrWhiteSpace(options.SecretKey) || options.TokenKeyVersion < 1)
        {
            return ValidateOptionsResult.Fail("Vendor configuration is incomplete.");
        }

        try
        {
            if (Convert.FromBase64String(options.AccessTokenEncryptionKey).Length != 32)
                return ValidateOptionsResult.Fail("Vendor access-token encryption key must decode to 32 bytes.");
        }
        catch (FormatException)
        {
            return ValidateOptionsResult.Fail("Vendor access-token encryption key must be Base64.");
        }

        return ValidateOptionsResult.Success;
    }
}
