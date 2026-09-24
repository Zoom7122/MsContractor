using DocumentRelationsGenerator.Cli;
using DocumentRelationsGenerator.Configuration;

namespace DocumentRelationsGenerator.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Settings_PreferTokenAndNormalizeBaseUrl()
    {
        var settings = GeneratorSettings.Load(new Dictionary<string, string>
        {
            ["MS_TOKEN"] = "tok", ["MS_LOGIN"] = "a@b", ["MS_PASSWORD"] = "p",
            ["MS_BASE_URL"] = "https://api.moysklad.ru/api/remap/1.2"
        });
        Assert.Equal("token", settings.Credentials!.Kind);
        Assert.Equal("https://api.moysklad.ru/api/remap/1.2/", settings.BaseUrl.ToString());
    }

    [Theory]
    [InlineData("http://api.moysklad.ru/api/remap/1.2/")]
    [InlineData("https://user:pass@api.moysklad.ru/api/remap/1.2/")]
    [InlineData("https://api.moysklad.ru/api/remap/1.1/")]
    public void Settings_RejectUnsafeBaseUrls(string url) =>
        Assert.Throws<GeneratorException>(() => GeneratorSettings.Load(new Dictionary<string, string> { ["MS_BASE_URL"] = url }));

    [Fact]
    public void Settings_AcceptMergeVerifierVariableNamesAndKeepPasswordVerbatim()
    {
        var settings = GeneratorSettings.Load(new Dictionary<string, string>
        {
            ["MOYSKLAD_LOGIN"] = "a@b", ["MOYSKLAD_PASSWORD"] = " spaced ", ["MS_RELTEST_RETAIL_STORE_ID"] = "8f0c7f6a-0000-4000-8000-000000000001"
        });
        Assert.Equal("login/password", settings.Credentials!.Kind);
        Assert.Equal("8f0c7f6a-0000-4000-8000-000000000001", settings.RetailStoreId);
        Assert.Null(GeneratorSettings.Load(new Dictionary<string, string>()).Credentials);
    }

    [Fact]
    public void EnvFile_ParsesQuotesCommentsAndExportAndEnvironmentWins()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, ["# comment", "export MS_LOGIN='user@example.com'", "MS_BASE_URL=https://x/api/remap/1.2 # trailing", "", "A=\"1\""]);
            var values = EnvFile.Merge(EnvFile.Read(file), new Dictionary<string, string> { ["A"] = "2" });
            Assert.Equal("user@example.com", values["MS_LOGIN"]);
            Assert.Equal("https://x/api/remap/1.2", values["MS_BASE_URL"]);
            Assert.Equal("2", values["A"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void CommandLine_ParsesSelectionSeedAndModes()
    {
        var options = CommandLineOptions.Parse(["--scenario", "sales-flow,purchase-flow", "--scenario", "retail-flow", "--seed", "42",
            "--anchor-date", "2026-09-24", "--counterparties", "2"]);
        Assert.Equal(["sales-flow", "purchase-flow", "retail-flow"], options.Scenarios);
        Assert.Equal(42, options.Seed);
        Assert.Equal(new DateOnly(2026, 9, 24), options.AnchorDate);
        Assert.Equal(2, options.Counterparties);
        Assert.True(CommandLineOptions.Parse(["--dry-run"]).DryRun);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--all --scenario sales-flow")]
    [InlineData("--cleanup m.json --all")]
    [InlineData("--seed abc --all")]
    [InlineData("--counterparties 9 --all")]
    [InlineData("--scenario")]
    [InlineData("--unknown")]
    public void CommandLine_RejectsInvalidCombinations(string args) =>
        Assert.Throws<GeneratorException>(() => CommandLineOptions.Parse(args.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
}
