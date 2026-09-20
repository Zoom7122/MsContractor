using System.Text.Json;
using MergeVerifier.Capture;
using MergeVerifier.Client;
using MergeVerifier.Models;
using MergeVerifier.Reporting;
using MergeVerifier.Verification;

var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    DotEnv.Load();
    if (args.Length == 0) throw new ArgumentException("Expected 'capture' or 'verify' command.");
    var parsed = Parse(args.Skip(1).ToArray());
    var egressUrl = Environment.GetEnvironmentVariable("MERGE_VERIFIER_EGRESS_URL") ?? "http://localhost:5012/";
    var apiKey = Environment.GetEnvironmentVariable("MERGE_VERIFIER_INTERNAL_API_KEY") ?? throw new ArgumentException(
        "Set MERGE_VERIFIER_INTERNAL_API_KEY to call Egress.");
    using var http = new HttpClient { BaseAddress = new Uri(egressUrl.EndsWith('/') ? egressUrl : egressUrl + "/"), Timeout = TimeSpan.FromMinutes(10) };
    var collector = new SnapshotCollector(new EgressClient(http, apiKey));
    switch (args[0])
    {
        case "capture":
        {
            var accountId = RequiredEnvironmentGuid("MERGE_VERIFIER_ACCOUNT_ID");
            var mainId = RequiredEnvironmentGuid("MERGE_VERIFIER_MAIN_KA");
            var duplicates = RequiredEnvironmentGuids("MERGE_VERIFIER_DUPLICATE_IDS");
            ValidateScope(mainId, duplicates);
            var snapshot = await collector.CollectAsync(accountId, mainId, duplicates, CancellationToken.None);
            var directory = Path.Combine("snapshots", $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-main_{mainId.ToString("N")[..8]}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "before.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, options));
            Console.WriteLine($"Snapshot saved: {path}");
            return 0;
        }
        case "verify":
        {
            var path = Required(parsed, "snapshot");
            var snapshotPath = Path.GetFullPath(path);
            if (!File.Exists(snapshotPath)) throw new ArgumentException($"Snapshot not found: {path}");
            var before = JsonSerializer.Deserialize<MergeSnapshot>(await File.ReadAllTextAsync(snapshotPath), options)
                ?? throw new InvalidOperationException("Snapshot is empty or invalid.");
            ValidateSnapshot(before);
            var after = await collector.CollectAsync(before.AccountId, before.MainCounterpartyId, before.DuplicateCounterpartyIds, CancellationToken.None);
            var directory = Path.GetDirectoryName(snapshotPath) ?? throw new InvalidOperationException("Snapshot directory is invalid.");
            await File.WriteAllTextAsync(Path.Combine(directory, "after.json"), JsonSerializer.Serialize(after, options));
            var report = new MergeVerificationService().Verify(before, after);
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(report, options));
            ConsoleReportWriter.Write(report);
            return report.Passed ? 0 : 1;
        }
        default: throw new ArgumentException("Expected 'capture' or 'verify' command.");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine($"MergeVerifier error: {exception.Message}");
    return 2;
}

static Dictionary<string, string> Parse(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (!values[index].StartsWith("--", StringComparison.Ordinal) || index + 1 == values.Length || values[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Options must use --name value syntax.");
        if (!result.TryAdd(values[index][2..], values[index + 1])) throw new ArgumentException($"Duplicate option: {values[index]}");
    }
    return result;
}
static string Required(IReadOnlyDictionary<string, string> values, string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Missing --{name}.");
static Guid RequiredEnvironmentGuid(string name) =>
    Guid.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value != Guid.Empty
        ? value
        : throw new ArgumentException($"{name} must be a non-empty GUID environment variable.");
static IReadOnlyList<Guid> RequiredEnvironmentGuids(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
        throw new ArgumentException($"Set {name} to a comma-separated list of non-empty GUIDs.");
    var result = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => Guid.TryParse(item, out var id) && id != Guid.Empty
            ? id
            : throw new ArgumentException($"{name} must contain only non-empty GUIDs."))
        .ToArray();
    return result.Length > 0 ? result : throw new ArgumentException($"{name} must contain at least one GUID.");
}
static void ValidateScope(Guid mainId, IReadOnlyList<Guid> duplicates)
{
    if (duplicates.Contains(mainId) || duplicates.Distinct().Count() != duplicates.Count) throw new ArgumentException("Duplicates must be unique and must not include main.");
}
static void ValidateSnapshot(MergeSnapshot snapshot)
{
    if (snapshot.SnapshotVersion != "1" || snapshot.AccountId == Guid.Empty || snapshot.MainCounterpartyId == Guid.Empty || snapshot.CapturedAt == default || snapshot.DuplicateCounterpartyIds.Count == 0) throw new InvalidOperationException("Snapshot integrity validation failed.");
    ValidateScope(snapshot.MainCounterpartyId, snapshot.DuplicateCounterpartyIds);
}

static class DotEnv
{
    public static void Load()
    {
        var path = FindPath();
        if (path is null) return;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();

            var separator = line.IndexOf('=');
            if (separator <= 0) throw new ArgumentException($"Invalid .env entry in {path}: {rawLine}");

            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '\"' && value[^1] == '\"')))
                value = value[1..^1];

            // Explicitly exported process variables have priority over the local file.
            if (Environment.GetEnvironmentVariable(name) is null)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string? FindPath()
    {
        var candidates = new[]
        {
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "MergeVerifier", ".env")
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
