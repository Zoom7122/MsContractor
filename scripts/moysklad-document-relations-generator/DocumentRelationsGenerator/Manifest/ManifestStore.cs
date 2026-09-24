using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentRelationsGenerator.Manifest;

public sealed class ManifestStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private int sequence;

    public ManifestStore(string path, RunManifest manifest)
    {
        Path = path;
        Manifest = manifest;
        sequence = manifest.Entities.Select(entity => entity.Sequence)
            .Concat(manifest.Counterparties.Select(entity => entity.Sequence))
            .Concat(manifest.Scenarios.SelectMany(scenario => scenario.Documents).Select(document => document.Sequence))
            .DefaultIfEmpty(0)
            .Max();
    }

    public string Path { get; }
    public RunManifest Manifest { get; }

    public int NextSequence() => ++sequence;

    /// <summary>Atomic replace: a crash never leaves a truncated manifest behind.</summary>
    public void Save() => WriteJsonAtomically(Path, Serialize(Manifest));

    public static string Serialize(RunManifest manifest) => JsonSerializer.Serialize(manifest, JsonOptions);

    public static RunManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<RunManifest>(json, JsonOptions) ?? throw new GeneratorException("Manifest is empty.");

    public static RunManifest Load(string path)
    {
        if (!File.Exists(path)) throw new GeneratorException($"Manifest not found: {path}");
        try
        {
            var manifest = Deserialize(File.ReadAllText(path));
            if (manifest.Tool != "moysklad-document-relations-generator" || string.IsNullOrEmpty(manifest.RunId))
                throw new GeneratorException($"{path} is not a manifest of this generator.");
            return manifest;
        }
        catch (JsonException ex)
        {
            throw new GeneratorException($"Manifest {path} is not valid JSON: {ex.Message}");
        }
    }

    public static void WriteJsonAtomically(string path, string json)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, json);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
