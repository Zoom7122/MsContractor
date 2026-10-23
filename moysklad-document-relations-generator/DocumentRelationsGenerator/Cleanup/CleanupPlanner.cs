using DocumentRelationsGenerator.Manifest;

namespace DocumentRelationsGenerator.Cleanup;

public sealed record CleanupItem(string Kind, string Role, string Type, string Id, string Href, string? Name, int Sequence,
    string? Scenario);

/// <summary>
/// Only objects recorded in the manifest as created by this run are deleted; reused account entities
/// (organization, existing store, expense item, retail store) never are. Order: documents children-first
/// (reverse creation order is a reverse topological order), then test entities in reverse creation order
/// (contracts before their counterparties).
/// </summary>
public static class CleanupPlanner
{
    public static IReadOnlyList<CleanupItem> Plan(RunManifest manifest)
    {
        var documents = manifest.Scenarios
            .SelectMany(scenario => scenario.Documents.Select(document => new CleanupItem("document", document.Key,
                document.Type, document.Id, document.Href, document.Name, document.Sequence, scenario.Name)))
            .OrderByDescending(item => item.Sequence);
        var entities = manifest.Counterparties.Concat(manifest.Entities)
            .Where(entity => entity.Created)
            .Select(entity => new CleanupItem("entity", entity.Role, entity.Type, entity.Id, entity.Href, entity.Name,
                entity.Sequence, null))
            .OrderByDescending(item => item.Sequence);
        return documents.Concat(entities).ToList();
    }
}
