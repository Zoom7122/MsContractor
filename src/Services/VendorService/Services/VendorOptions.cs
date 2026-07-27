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
    public Uri AppsApiBaseUrl { get; init; } = new("https://apps-api.moysklad.ru/api/vendor/1.0/");
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromHours(8);
    public string SessionCookieName { get; init; } = "mscontractor.session";
}

public sealed class VendorOptionsValidator : IValidateOptions<VendorOptions>
{
    public ValidateOptionsResult Validate(string? name, VendorOptions options)
    {
        if (options.AppId == Guid.Empty || string.IsNullOrWhiteSpace(options.AppUid) ||
            string.IsNullOrWhiteSpace(options.SecretKey) || options.TokenKeyVersion < 1 ||
            !options.AppsApiBaseUrl.IsAbsoluteUri ||
            options.AppsApiBaseUrl.Scheme != Uri.UriSchemeHttps ||
            options.SessionLifetime <= TimeSpan.Zero ||
            options.SessionLifetime > TimeSpan.FromDays(1) ||
            string.IsNullOrWhiteSpace(options.SessionCookieName))
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
