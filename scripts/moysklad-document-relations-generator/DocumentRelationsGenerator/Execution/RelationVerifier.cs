using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;
using DocumentRelationsGenerator.Relations;

namespace DocumentRelationsGenerator.Execution;

/// <summary>
/// Proves persisted relations with fresh GETs: a 200/201 on create is not evidence. The relation is checked
/// on the side that stores it (source); a documented reverse field is checked too and reported as a warning.
/// </summary>
public sealed class RelationVerifier
{
    private readonly IMoySkladApi api;
    private readonly TextWriter output;

    public RelationVerifier(IMoySkladApi api, TextWriter output)
    {
        this.api = api;
        this.output = output;
    }

    public static bool SourceHasTarget(JsonObject source, RelationDefinition relation, string targetHref)
    {
        var node = source[relation.Kind == RelationKind.Operation ? "operations" : relation.Field];
        return References(node).Any(href => MetaReference.SameEntity(href, targetHref));
    }

    /// <summary>Null when the documentation describes no reverse field for this relation.</summary>
    public static bool? ReverseHasSource(JsonObject target, RelationDefinition relation, string sourceHref) =>
        relation.ReverseField is null
            ? null
            : References(target[relation.ReverseField]).Any(href => MetaReference.SameEntity(href, sourceHref));

    public async Task VerifyAsync(ManifestScenario result, IReadOnlyDictionary<string, JsonObject> created,
        string counterpartyHref, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        async Task<JsonObject> FetchAsync(string href)
        {
            if (cache.TryGetValue(href, out var cached)) return cached;
            var document = await api.GetAsync(href, cancellationToken);
            cache[href] = document;
            return document;
        }

        foreach (var document in result.Documents.Where(item => DocumentFieldSupport.AgentBearing.Contains(item.Type)))
        {
            try
            {
                var fresh = await FetchAsync(document.Href);
                if (!MetaReference.SameEntity(MetaReference.Href(fresh["agent"]), counterpartyHref))
                {
                    result.Errors.Add(new ManifestError
                    {
                        Scenario = result.Name, Step = document.Key, DocumentType = document.Type, Operation = "GET verify agent",
                        Message = "agent of the saved document is not the scenario counterparty"
                    });
                    output.WriteLine($"  [FAIL] {document.Key}: agent is not the scenario counterparty");
                }
            }
            catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure)
            {
                result.Errors.Add(ToError(result.Name, document.Key, document.Type, "GET verify", ex));
            }
        }

        foreach (var relation in result.Relations)
        {
            var definition = RelationCatalog.Get(relation.RelationId);
            if (!created.TryGetValue(relation.SourceStep, out var source) || !created.TryGetValue(relation.TargetStep, out var target))
            {
                relation.Status = "not-created";
                relation.Detail ??= "source or target document was not created";
                output.WriteLine($"  [FAIL] {definition.Label}: not created");
                continue;
            }

            var sourceHref = MetaReference.Href(source)!;
            var targetHref = MetaReference.Href(target)!;
            try
            {
                relation.Verified = SourceHasTarget(await FetchAsync(sourceHref), definition, targetHref);
                relation.Status = relation.Verified ? "verified" : "failed";
                if (!relation.Verified)
                    relation.Detail = $"GET {definition.SourceType}/{relation.SourceId}: '{definition.Field}' does not reference {definition.TargetType}/{relation.TargetId}";
                output.WriteLine($"  [{(relation.Verified ? "OK" : "FAIL")}] {definition.Label}");

                if (definition.ReverseField is not null)
                {
                    relation.ReverseVerified = ReverseHasSource(await FetchAsync(targetHref), definition, sourceHref);
                    if (relation.ReverseVerified == false)
                    {
                        var warning = $"{definition.TargetType}.{definition.ReverseField} does not list {definition.SourceType}/{relation.SourceId}";
                        result.Warnings.Add(warning);
                        output.WriteLine($"  [WARN] reverse: {warning}");
                    }
                }
            }
            catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure)
            {
                relation.Status = "failed";
                relation.Detail = $"verification GET failed: {ex.Message}";
                output.WriteLine($"  [FAIL] {definition.Label}: {ex.Message}");
            }
        }
    }

    public static ManifestError ToError(string scenario, string step, string type, string operation, MoySkladApiException ex,
        JsonObject? payload = null) => new()
    {
        Scenario = scenario,
        Step = step,
        DocumentType = type,
        Operation = operation,
        HttpStatus = ex.StatusCode,
        ErrorCode = ex.ErrorCode,
        Message = ex.ErrorMessage,
        OutcomeUnknown = ex.OutcomeUnknown,
        RequestPayload = payload
    };

    private static IEnumerable<string> References(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(MetaReference.Href).OfType<string>(),
        JsonObject { } obj when obj["rows"] is JsonArray rows => rows.Select(MetaReference.Href).OfType<string>(),
        JsonObject obj => MetaReference.Href(obj) is { } href ? [href] : [],
        _ => []
    };
}
