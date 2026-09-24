namespace DocumentRelationsGenerator.Configuration;

/// <summary>
/// Minimal KEY=VALUE reader (comments, blank lines, <c>export</c>, single/double quotes).
/// Values are returned, never printed; the process environment is not modified.
/// </summary>
public static class EnvFile
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new GeneratorException($"{Path.GetFileName(path)}: line {lineNumber} is not KEY=VALUE.");
            var key = line[..separator].Trim();
            values[key] = Unquote(line[(separator + 1)..].Trim());
        }

        return values;
    }

    /// <summary>File values first, then the process environment on top (environment wins).</summary>
    public static IReadOnlyDictionary<string, string> Merge(IReadOnlyDictionary<string, string>? file,
        IReadOnlyDictionary<string, string> environment)
    {
        var merged = new Dictionary<string, string>(file ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var (key, value) in environment)
            if (!string.IsNullOrEmpty(value)) merged[key] = value;
        return merged;
    }

    public static IReadOnlyDictionary<string, string> ProcessEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) result[key] = value;
        return result;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            return value[1..^1];
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? value[..comment].TrimEnd() : value;
    }
}
