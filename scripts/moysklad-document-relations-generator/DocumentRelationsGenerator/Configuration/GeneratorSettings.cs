using System.Globalization;
using DocumentRelationsGenerator.MoySklad;

namespace DocumentRelationsGenerator.Configuration;

/// <summary>
/// Settings read from environment variables (and an optional env file). Variable names match
/// scripts_for_test_data (MS_TOKEN, MS_LOGIN, MS_PASSWORD, MS_BASE_URL); MergeVerifier names
/// (MOYSKLAD_*) are accepted as fallbacks.
/// </summary>
public sealed class GeneratorSettings
{
    public const string DefaultBaseUrl = "https://api.moysklad.ru/api/remap/1.2/";

    public required Uri BaseUrl { get; init; }

    /// <summary>Null when no credentials are configured; only an offline dry-run can continue.</summary>
    public MoySkladCredentials? Credentials { get; init; }

    public string? OrganizationId { get; init; }
    public string? StoreId { get; init; }
    public string? RetailStoreId { get; init; }
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(400);

    public static GeneratorSettings Load(IReadOnlyDictionary<string, string> values)
    {
        string? Value(params string[] names) => names
            .Select(name => values.TryGetValue(name, out var value) ? value.Trim() : null)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));

        var url = Value("MS_BASE_URL", "MOYSKLAD_BASE_URL") ?? DefaultBaseUrl;
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != "https" ||
            baseUrl.UserInfo.Length != 0 || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0 ||
            !baseUrl.AbsolutePath.EndsWith("/api/remap/1.2/", StringComparison.Ordinal))
            throw new GeneratorException("MS_BASE_URL must be an HTTPS JSON API 1.2 URL without credentials, query or fragment.");

        // Passwords are used verbatim: surrounding spaces may be part of the secret.
        var password = new[] { "MS_PASSWORD", "MOYSKLAD_PASSWORD" }
            .Select(name => values.TryGetValue(name, out var value) ? value : null)
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));
        var token = Value("MS_TOKEN");
        var login = Value("MS_LOGIN", "MOYSKLAD_LOGIN");
        MoySkladCredentials? credentials = null;
        if (token is not null) credentials = MoySkladCredentials.FromToken(token);
        else if (login is not null || password is not null) credentials = MoySkladCredentials.FromLogin(login ?? "", password ?? "");

        var interval = TimeSpan.FromMilliseconds(400);
        if (Value("MS_RELTEST_MIN_REQUEST_INTERVAL_MS") is { } rawInterval)
        {
            if (!int.TryParse(rawInterval, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) ||
                milliseconds < 100)
                throw new GeneratorException("MS_RELTEST_MIN_REQUEST_INTERVAL_MS must be an integer >= 100.");
            interval = TimeSpan.FromMilliseconds(milliseconds);
        }

        return new GeneratorSettings
        {
            BaseUrl = baseUrl,
            Credentials = credentials,
            OrganizationId = Guid(Value("MS_RELTEST_ORGANIZATION_ID"), "MS_RELTEST_ORGANIZATION_ID"),
            StoreId = Guid(Value("MS_RELTEST_STORE_ID"), "MS_RELTEST_STORE_ID"),
            RetailStoreId = Guid(Value("MS_RELTEST_RETAIL_STORE_ID"), "MS_RELTEST_RETAIL_STORE_ID"),
            MinRequestInterval = interval
        };
    }

    private static string? Guid(string? value, string name)
    {
        if (value is null) return null;
        if (!System.Guid.TryParse(value, out var id)) throw new GeneratorException($"{name} must be a UUID.");
        return id.ToString("D");
    }
}
