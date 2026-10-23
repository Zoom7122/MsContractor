using System.Text.RegularExpressions;
using DocumentRelationsGenerator.Relations;
using DocumentRelationsGenerator.Scenarios;

namespace DocumentRelationsGenerator.Tests;

/// <summary>The code catalog and DOCUMENT_RELATIONS.md must describe the same relations.</summary>
public sealed class DocumentRelationsMarkdownTests
{
    private static readonly Regex Row = new(@"^\| `(?<id>[a-z]+\.[A-Za-z]+->[a-z]+)` \|.*\| (?<scenarios>[a-z, -]+) \| [^|]+ \|$",
        RegexOptions.Multiline);

    /// <summary>Only the "Подтверждённые связи" section: other tables list relations that are not generated.</summary>
    private static string ReadDocument()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "DOCUMENT_RELATIONS.md");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            var start = text.IndexOf("## Подтверждённые связи", StringComparison.Ordinal);
            var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
            return text[start..end];
        }

        throw new FileNotFoundException("DOCUMENT_RELATIONS.md not found above the test directory.");
    }

    [Fact]
    public void ConfirmedTable_MatchesTheCodeCatalog()
    {
        var ids = Row.Matches(ReadDocument()).Select(match => match.Groups["id"].Value).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(RelationCatalog.All.Select(relation => relation.Id).Order(), ids.Order());
    }

    [Fact]
    public void ScenarioColumn_MatchesTheRegistry()
    {
        foreach (Match match in Row.Matches(ReadDocument()))
        {
            var id = match.Groups["id"].Value;
            var documented = match.Groups["scenarios"].Value.Split(',', StringSplitOptions.TrimEntries).Order().ToList();
            var actual = ScenarioRegistry.All
                .Where(scenario => scenario.Steps.SelectMany(step => step.Links).Any(link => link.RelationId == id))
                .Select(scenario => scenario.Name).Order().ToList();
            Assert.True(documented.SequenceEqual(actual), $"{id}: documented [{string.Join(", ", documented)}], code [{string.Join(", ", actual)}]");
        }
    }
}
