namespace MergeVerifier.Configuration;

public sealed class MergeVerifierOptions
{
    public const string DefaultBaseUrl = "https://api.moysklad.ru/api/remap/1.2/";
    // Credentials deliberately do not belong to this serializable configuration model.
    public required Uri BaseUrl { get; init; }
    public required string SnapshotDirectory { get; init; }

    public static MergeVerifierOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("MOYSKLAD_BASE_URL") ?? DefaultBaseUrl;
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith("/api/remap/1.2/", StringComparison.Ordinal))
            throw new VerifierException("MOYSKLAD_BASE_URL must be an HTTPS JSON API 1.2 URL without credentials, query or fragment.");
        return new()
        {
            BaseUrl = uri,
            SnapshotDirectory = Environment.GetEnvironmentVariable("MERGE_VERIFIER_SNAPSHOT_DIR") ?? "snapshots"
        };
    }
}
