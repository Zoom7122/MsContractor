using System.Text.Json;
using System.Text.Json.Serialization;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.Normalization;

namespace MergeVerifier.Storage;

public static class SnapshotStore
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static async Task<MergeSnapshot> ReadAsync(string path, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        RejectDuplicateProperties(json.RootElement);
        if (!json.RootElement.TryGetProperty("snapshotVersion", out var version) || !version.TryGetInt32(out var number) ||
            number != MergeSnapshot.CurrentVersion)
            throw new VerifierException($"Unsupported or missing snapshotVersion; expected {MergeSnapshot.CurrentVersion}. " +
                                        "A BEFORE made by an earlier version is normalized differently: capture it again.");
        var result = json.Deserialize<MergeSnapshot>(JsonOptions) ?? throw new VerifierException("Invalid snapshot.");
        Validate(result);
        return result;
    }

    public static void Validate(MergeSnapshot snapshot)
    {
        if (snapshot.SnapshotVersion != MergeSnapshot.CurrentVersion)
            throw new VerifierException($"Unsupported snapshotVersion; expected {MergeSnapshot.CurrentVersion}.");
        ValidateScope(snapshot.MainCounterpartyId, snapshot.DuplicateCounterpartyIds);
        if (snapshot.CaptureStartedAt == default || snapshot.CaptureCompletedAt < snapshot.CaptureStartedAt ||
            snapshot.Documents is null || snapshot.Coverage is null)
            throw new VerifierException("Invalid snapshot metadata.");
        if (snapshot.Coverage.Count != DocumentRegistry.All.Count ||
            snapshot.Coverage.Select(x => x.EntityType).Distinct().Count() != DocumentRegistry.All.Count)
            throw new VerifierException("Snapshot lacks complete document-type coverage.");
        foreach (var coverage in snapshot.Coverage)
        {
            var rule = DocumentRegistry.Get(coverage.EntityType);
            if (coverage.Fetched != (rule.TransferMode != DocumentTransferMode.Unsupported))
                throw new VerifierException("Snapshot has incomplete or inconsistent fetch coverage.");
        }
        var seen = new HashSet<(string, Guid)>();
        foreach (var document in snapshot.Documents)
        {
            if (document is null) throw new VerifierException("Null document in snapshot.");
            var rule = DocumentRegistry.Get(document.EntityType);
            if (document.TransferMode != rule.TransferMode || rule.TransferMode == DocumentTransferMode.Unsupported ||
                document.Data.ValueKind != JsonValueKind.Object ||
                document.SourceCounterpartyId != snapshot.MainCounterpartyId && !snapshot.DuplicateCounterpartyIds.Contains(document.SourceCounterpartyId))
                throw new VerifierException("Invalid document rule, owner or data in snapshot.");
            if (rule.TransferMode == DocumentTransferMode.Recreate)
            {
                if (document.StableDocumentId is not null ||
                    DocumentNormalizer.RecreatedTechnicalFields.Any(field => document.Data.TryGetProperty(field, out _)))
                    throw new VerifierException("Recreated document contains technical identity data.");
            }
            else if (!Guid.TryParse(document.StableDocumentId, out var id) || id == Guid.Empty || !seen.Add((rule.EntityType, id)))
                throw new VerifierException("Invalid or duplicate stable document identity in snapshot.");
            foreach (var field in DocumentNormalizer.RootTechnicalFields)
                if (document.Data.TryGetProperty(field, out _)) throw new VerifierException("Snapshot document is not normalized.");
            foreach (var collection in rule.PositionCollections)
            {
                if (!document.Data.TryGetProperty(collection, out var array) || array.ValueKind != JsonValueKind.Array)
                    throw new VerifierException("Snapshot lacks complete positions.");
                foreach (var position in array.EnumerateArray())
                    if (position.ValueKind != JsonValueKind.Object || position.TryGetProperty("id", out _) ||
                        position.TryGetProperty("meta", out _) || position.TryGetProperty("accountId", out _))
                        throw new VerifierException("Snapshot has invalid normalized positions.");
            }
            RejectDuplicateProperties(document.Data);
        }
    }

    public static void ValidateScope(Guid main, IReadOnlyList<Guid>? duplicates)
    {
        if (main == Guid.Empty || duplicates is null || duplicates.Count == 0 || duplicates.Contains(Guid.Empty) ||
            duplicates.Contains(main) || duplicates.Distinct().Count() != duplicates.Count)
            throw new VerifierException("Scope requires a main UUID and distinct non-empty duplicate UUIDs excluding main.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new VerifierException("JSON contains duplicate property names.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    public static async Task WriteAsync<T>(string path, T value, bool overwrite, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".merge-verifier-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, ct);
            File.Move(temporary, full, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
