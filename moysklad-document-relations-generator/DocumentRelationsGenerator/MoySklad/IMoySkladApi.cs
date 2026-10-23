using System.Text.Json.Nodes;

namespace DocumentRelationsGenerator.MoySklad;

public interface IMoySkladApi
{
    Uri BaseUrl { get; }

    /// <summary>GET a relative API path (<c>entity/...</c>, <c>context/...</c>) or an API href.</summary>
    Task<JsonObject> GetAsync(string pathOrHref, CancellationToken cancellationToken);

    /// <summary>
    /// <c>PUT /entity/{type}/new</c>: returns a prefilled template. MoySklad documents that this request
    /// creates nothing, so it is safe to repeat.
    /// </summary>
    Task<JsonObject> GetTemplateAsync(string entityType, JsonObject baseDocuments, CancellationToken cancellationToken);

    /// <summary>
    /// <c>POST /entity/{type}</c>. Repeated after a transport failure only when the payload carries a
    /// <c>syncId</c>; otherwise the failure is reported as an unknown outcome.
    /// </summary>
    Task<JsonObject> CreateAsync(string entityType, JsonObject payload, CancellationToken cancellationToken);

    Task DeleteAsync(string pathOrHref, CancellationToken cancellationToken);
}
