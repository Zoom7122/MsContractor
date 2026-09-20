using MergeVerifier.Storage;

namespace MergeVerifier.Cli;

public sealed record CommandLine(string Command, Guid Main, IReadOnlyList<Guid> Duplicates, string? SnapshotPath)
{
    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h" or "help") return new("help", default, [], null);
        if (args.Length == 0 || args[0] is not ("capture" or "verify") || args.Length % 2 == 0)
            throw new VerifierException("Usage: capture --main UUID --duplicates UUID,UUID | verify --snapshot path/to/before.json");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (!options.TryAdd(args[i], args[i + 1])) throw new VerifierException("Repeated CLI option.");
        if (args[0] == "capture")
        {
            if (options.Count != 2 || !options.TryGetValue("--main", out var mainText) ||
                !Guid.TryParse(mainText, out var main) || !options.TryGetValue("--duplicates", out var duplicateText))
                throw new VerifierException("capture requires --main UUID and --duplicates UUID,UUID.");
            var duplicates = duplicateText.Split(',').Select(x => Guid.TryParse(x.Trim(), out var id) && id != Guid.Empty
                ? id : throw new VerifierException("Invalid duplicate UUID.")).Distinct().ToArray();
            SnapshotStore.ValidateScope(main, duplicates);
            return new("capture", main, duplicates, null);
        }
        if (options.Count != 1 || !options.TryGetValue("--snapshot", out var path) ||
            string.IsNullOrWhiteSpace(path) || Path.GetFileName(path) != "before.json")
            throw new VerifierException("verify requires --snapshot pointing to before.json.");
        return new("verify", default, [], Path.GetFullPath(path));
    }
}
