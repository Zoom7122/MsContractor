using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentRelationsGenerator.Manifest;
using DocumentRelationsGenerator.MoySklad;

namespace DocumentRelationsGenerator.Cleanup;

public sealed class CleanupResult
{
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Scenario { get; set; }

    /// <summary>deleted, already-deleted, in-use, not-deletable, identity-mismatch, failed.</summary>
    public string Outcome { get; set; } = "pending";

    public int? HttpStatus { get; set; }
    public int? ErrorCode { get; set; }
    public string? Message { get; set; }
}

public sealed class CleanupReport
{
    public string RunId { get; set; } = "";
    public string Manifest { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<CleanupResult> Items { get; set; } = [];
    public bool Complete => Items.All(item => item.Outcome is "deleted" or "already-deleted");
}

public sealed class CleanupRunner
{
    private const int Passes = 2;
    private readonly IMoySkladApi api;
    private readonly TextWriter output;

    public CleanupRunner(IMoySkladApi api, TextWriter output)
    {
        this.api = api;
        this.output = output;
    }

    public async Task<CleanupReport> RunAsync(RunManifest manifest, string manifestPath, Func<DateTimeOffset> now,
        CancellationToken cancellationToken)
    {
        var report = new CleanupReport { RunId = manifest.RunId, Manifest = manifestPath, StartedAt = now() };
        var plan = CleanupPlanner.Plan(manifest);
        var results = plan.ToDictionary(item => item, item => new CleanupResult
        {
            Kind = item.Kind, Type = item.Type, Id = item.Id, Name = item.Name, Scenario = item.Scenario
        });
        report.Items = results.Values.ToList();

        for (var pass = 1; pass <= Passes; pass++)
        {
            // A document still referenced by another one (409) gets a second chance after the others are gone.
            var pending = plan.Where(item => results[item].Outcome is "pending" or "in-use" or "failed").ToList();
            if (pending.Count == 0) break;
            if (pass > 1) output.WriteLine($"Pass {pass}: retrying {pending.Count} object(s)");
            foreach (var item in pending) await DeleteAsync(item, results[item], manifest.RunId, cancellationToken);
        }

        report.FinishedAt = now();
        return report;
    }

    public static void Print(CleanupReport report, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("====================================");
        output.WriteLine("CLEANUP REPORT");
        output.WriteLine("====================================");
        foreach (var group in report.Items.GroupBy(item => item.Outcome).OrderBy(group => group.Key))
            output.WriteLine($"{group.Key,-18} {group.Count()}");
        foreach (var item in report.Items.Where(item => item.Outcome is not ("deleted" or "already-deleted")))
            output.WriteLine($"  {item.Outcome}: {item.Type} {item.Id} {item.Name} " +
                             $"status={item.HttpStatus?.ToString() ?? "-"} code={item.ErrorCode?.ToString() ?? "-"} {item.Message}");
        output.WriteLine(report.Complete ? "All manifest objects are gone." : "Some objects remain; see the list above.");
    }

    public static string Serialize(CleanupReport report) => JsonSerializer.Serialize(report, ManifestStore.JsonOptions);

    private async Task DeleteAsync(CleanupItem item, CleanupResult result, string runId, CancellationToken cancellationToken)
    {
        try
        {
            // Guard against a stale or edited manifest: delete only objects that still carry this run's marker.
            JsonObject current;
            try
            {
                current = await api.GetAsync(item.Href, cancellationToken);
            }
            catch (MoySkladApiException ex) when (ex.StatusCode == 404)
            {
                Set(result, "already-deleted", ex);
                return;
            }

            if (!HasRunMarker(current, runId))
            {
                result.Outcome = "identity-mismatch";
                result.Message = "name/externalCode no longer contains the run ID; not deleted";
                output.WriteLine($"  [REFUSED] {item.Type} {item.Id}: {result.Message}");
                return;
            }

            await api.DeleteAsync(item.Href, cancellationToken);
            result.Outcome = "deleted";
            result.HttpStatus = 200;
            output.WriteLine($"  [DELETED] {item.Type,-20} {item.Name} ({item.Id})");
        }
        catch (MoySkladApiException ex) when (!ex.IsAuthenticationFailure)
        {
            var outcome = ex.StatusCode switch
            {
                404 => "already-deleted",
                409 => "in-use",
                405 => "not-deletable",
                _ => "failed"
            };
            Set(result, outcome, ex);
            if (outcome != "already-deleted")
                output.WriteLine($"  [{outcome.ToUpperInvariant()}] {item.Type} {item.Id}: HTTP {ex.StatusCode?.ToString() ?? "-"} " +
                                 $"code {ex.ErrorCode?.ToString() ?? "-"} {ex.ErrorMessage}");
        }
    }

    private static bool HasRunMarker(JsonObject entity, string runId) =>
        new[] { "externalCode", "name" }.Any(field =>
            entity[field] is JsonValue value && value.TryGetValue<string>(out var text) &&
            text.Contains(runId, StringComparison.Ordinal));

    private static void Set(CleanupResult result, string outcome, MoySkladApiException ex)
    {
        result.Outcome = outcome;
        result.HttpStatus = ex.StatusCode;
        result.ErrorCode = ex.ErrorCode;
        result.Message = ex.ErrorMessage;
    }
}
