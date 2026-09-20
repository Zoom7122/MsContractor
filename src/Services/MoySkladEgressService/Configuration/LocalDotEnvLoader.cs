namespace MsContractor.MoySkladEgressService.Configuration;

public static class LocalDotEnvLoader
{
    public static void LoadIfPresent(params string[] paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                    continue;

                var name = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                if (Environment.GetEnvironmentVariable(name) is null)
                    Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
            }
        }
    }
}
