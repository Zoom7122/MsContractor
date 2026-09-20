using System.Text.Json.Nodes;

namespace MergeVerifier.MoySklad;

public interface IMoySkladClient
{
    Uri BaseUrl { get; }
    Task<JsonObject> GetAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<JsonObject>> GetAllAsync(string path, CancellationToken cancellationToken);
}
