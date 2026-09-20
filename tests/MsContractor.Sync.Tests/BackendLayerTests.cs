using System.Text.RegularExpressions;

namespace MsContractor.Sync.Tests;

public sealed class BackendLayerTests
{
    [Fact]
    public void BackendSources_KeepPersistenceAndTransportInTheirLayers()
    {
        var root = FindRoot();
        var allowed = new HashSet<string>
        {
            "Controllers", "Consumers", "Services", "Repositories", "Persistence", "Clients", "Gateways",
            "Messaging", "Contracts", "Models", "Middleware", "HealthChecks", "RateLimiting",
            "ResponseHandling", "Validation", "Configuration"
        };
        foreach (var service in Directory.EnumerateDirectories(Path.Combine(root, "src", "Services")))
        {
            Assert.False(Directory.Exists(Path.Combine(service, "Repo")), service);
            foreach (var path in Directory.EnumerateFiles(service, "*.cs", SearchOption.AllDirectories))
            {
                var parts = Path.GetRelativePath(service, path).Split(Path.DirectorySeparatorChar);
                if (parts[0] is "obj" or "bin") continue;
                if (parts.Length == 1)
                {
                    Assert.Equal("Program.cs", parts[0]);
                    continue;
                }
                Assert.Contains(parts[0], allowed);
                var source = File.ReadAllText(path);
                var expectedNamespace = "MsContractor." + Path.GetFileName(service) + "." + string.Join('.', parts[..^1]);
                Assert.Matches(@"\bnamespace\s+" + Regex.Escape(expectedNamespace) + @"\s*[;{]", source);
                if (parts[0] is "Controllers" or "Consumers" or "Services" or "Messaging")
                    Assert.DoesNotMatch(@"\b(DbContext|DbSet|IQueryable|ChangeTracker)\b|Microsoft\.EntityFrameworkCore|\w+DbContext\b|\.Persistence\s*;", source);
                if (parts[0] is "Controllers" or "Services")
                    Assert.DoesNotMatch(@"using\s+(Confluent\.Kafka|StackExchange\.Redis)|\bHttpClient\b", source);
                if (parts[0] == "Repositories")
                    Assert.DoesNotMatch(@"^\s*public\s+[^\r\n]*(IQueryable|DbSet|IDbContextTransaction)", source);
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MsContractor.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }
}
