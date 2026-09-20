using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Comparison;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.Normalization;
using MergeVerifier.Reporting;
using MergeVerifier.Storage;
using Xunit;
using static MergeVerifier.Tests.Fixtures;

namespace MergeVerifier.Tests;

public sealed class ComparisonTests
{
    private static TypeComparisonResult Compare(string type, DocumentSnapshot[] before, DocumentSnapshot[] after) =>
        DocumentComparisonService.For(DocumentRegistry.Get(type)).Compare(before, after, Main);

    [Theory]
    [InlineData("customerorder")]
    [InlineData("salesreturn")]
    [InlineData("purchasereturn")]
    [InlineData("retailsalesreturn")]
    public async Task AgentMovedAndBusinessPreservedPasses(string type)
    {
        var before = await Document(Raw(type), type);
        var after = await Document(Raw(type, agent: Main), type);
        var result = Compare(type, [before], [after]);
        Assert.True(result.Passed);
        Assert.Equal(1, result.Count(VerificationStatus.Matched));
    }

    [Theory]
    [InlineData("customerorder")]
    [InlineData("salesreturn")]
    public async Task RemainingOnDuplicateFails(string type)
    {
        var doc = await Document(Raw(type), type);
        var result = Compare(type, [doc], [doc]);
        Assert.False(result.Passed);
        Assert.Equal(1, result.Count(VerificationStatus.StillOnDuplicate));
        Assert.Equal(1, result.Count(VerificationStatus.Missing));
    }

    [Theory]
    [InlineData("customerorder")]
    [InlineData("salesreturn")]
    public async Task DisappearedDocumentIsMissing(string type)
    {
        var result = Compare(type, [await Document(Raw(type), type)], []);
        Assert.Equal(1, result.Count(VerificationStatus.Missing));
        Assert.NotEmpty(result.Documents.Single().Differences);
    }

    [Fact]
    public async Task PutIdChangeProducesMissingAndUnexpected()
    {
        var result = Compare("customerorder", [await Document(Raw())], [await Document(Raw(id: OtherId, agent: Main))]);
        Assert.Equal(1, result.Count(VerificationStatus.Missing));
        Assert.Equal(1, result.Count(VerificationStatus.Unexpected));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("code")]
    [InlineData("externalCode")]
    [InlineData("description")]
    [InlineData("unknownBusinessField")]
    [InlineData("created")]
    public async Task PutBusinessFieldChangesAreReported(string field)
    {
        var after = Raw(agent: Main); after[field] = "changed";
        var result = Compare("customerorder", [await Document(Raw())], [await Document(after)]);
        Assert.Equal(1, result.Count(VerificationStatus.Changed));
        Assert.Contains(result.Documents.Single().Differences, x => x.Path == "/" + field);
    }

    [Theory]
    [InlineData("customerorder")]
    [InlineData("salesreturn")]
    public async Task PositionOrderAndOwnIdsDoNotMatter(string type)
    {
        var before = await Document(Raw(type, quantities: [1, 2, 1]), type);
        var after = await Document(Raw(type, agent: Main, quantities: [1, 1, 2]), type);
        Assert.True(Compare(type, [before], [after]).Passed);
    }

    [Fact]
    public async Task PositionQuantityAndMultiplicityMatter()
    {
        var before = await Document(Raw(quantities: [1, 1]));
        foreach (var quantities in new[] { new[] { 1 }, new[] { 1, 2 } })
        {
            var result = Compare("customerorder", [before], [await Document(Raw(agent: Main, quantities: quantities))]);
            Assert.Equal(1, result.Count(VerificationStatus.Changed));
            Assert.Contains(result.Documents.Single().Differences, x => x.Path == "/positions");
        }
    }

    [Fact]
    public async Task SecondCommissionPositionCollectionIsCompared()
    {
        var after = Raw("commissionreportin", agent: Main);
        after["returnToCommissionerPositions"]![0]!["quantity"] = 5;
        var result = Compare("commissionreportin", [await Document(Raw("commissionreportin"), "commissionreportin")],
            [await Document(after, "commissionreportin")]);
        Assert.Equal(1, result.Count(VerificationStatus.Changed));
    }

    [Fact]
    public async Task RecreateDifferentIdAndTechnicalTimestampsPass()
    {
        var before = await Document(Raw("salesreturn"), "salesreturn");
        var raw = Raw("salesreturn", OtherId, Main); raw["created"] = "later"; raw["updated"] = "later";
        Assert.True(Compare("salesreturn", [before], [await Document(raw, "salesreturn")]).Passed);
    }

    [Theory]
    [InlineData(2, 2, 0, 0)]
    [InlineData(2, 1, 1, 0)]
    [InlineData(1, 2, 0, 1)]
    [InlineData(0, 1, 0, 1)]
    public async Task RecreatedDocumentsAreMultisets(int beforeCount, int afterCount, int missing, int unexpected)
    {
        var before = await Document(Raw("salesreturn"), "salesreturn");
        var after = await Document(Raw("salesreturn", OtherId, Main), "salesreturn");
        var result = Compare("salesreturn", Enumerable.Repeat(before, beforeCount).ToArray(), Enumerable.Repeat(after, afterCount).ToArray());
        Assert.Equal(missing, result.Count(VerificationStatus.Missing));
        Assert.Equal(unexpected, result.Count(VerificationStatus.Unexpected));
        Assert.Equal(Math.Min(beforeCount, afterCount), result.Count(VerificationStatus.Matched));
    }

    [Fact]
    public async Task RecreateChangedBusinessProducesStructuredCandidateDiff()
    {
        var raw = Raw("salesreturn", OtherId, Main); raw["sum"] = 200;
        var result = Compare("salesreturn", [await Document(Raw("salesreturn"), "salesreturn")], [await Document(raw, "salesreturn")]);
        Assert.Equal(1, result.Count(VerificationStatus.Missing));
        Assert.Equal(1, result.Count(VerificationStatus.Unexpected));
        Assert.Contains(result.Documents.Single(x => x.Status == VerificationStatus.Missing).Differences, x => x.Path == "/sum");
    }

    [Fact]
    public async Task RecreatedIdAbsentFromSnapshotReportAndConsole()
    {
        var doc = await Document(Raw("salesreturn"), "salesreturn");
        var report = DocumentComparisonService.Compare(Snapshot(doc), Snapshot());
        var console = new StringWriter(); ConsoleReportWriter.Write(report, console);
        foreach (var text in new[] { JsonSerializer.Serialize(Snapshot(doc), SnapshotStore.JsonOptions),
                     JsonSerializer.Serialize(report, SnapshotStore.JsonOptions), console.ToString() })
            Assert.DoesNotContain(Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.Null(doc.StableDocumentId);
    }

    [Fact]
    public void CanonicalPropertiesAndNumbersAreStable()
    {
        var left = JsonNode.Parse("{\"b\":1.000,\"a\":100000000000000000000000000001,\"c\":-0}");
        var right = JsonNode.Parse("{\"c\":0.0,\"a\":100000000000000000000000000001.0,\"b\":1e0}");
        Assert.Equal(JsonCanonicalizer.Canonicalize(left), JsonCanonicalizer.Canonicalize(right));
        Assert.Equal(SemanticHasher.Hash(left), SemanticHasher.Hash(right));
        Assert.NotEqual(SemanticHasher.Hash(JsonNode.Parse("100000000000000000000000000001")),
            SemanticHasher.Hash(JsonNode.Parse("100000000000000000000000000002")));
    }

    [Fact]
    public async Task BusinessReferencesArePreservedAndCompared()
    {
        var beforeRaw = Raw("salesreturn"); beforeRaw["demand"] = Reference("demand", OtherId);
        var afterRaw = Raw("salesreturn", agent: Main); afterRaw["demand"] = Reference("demand", Main);
        var before = await Document(beforeRaw, "salesreturn");
        Assert.Contains(OtherId.ToString(), before.Data.ToString());
        Assert.False(Compare("salesreturn", [before], [await Document(afterRaw, "salesreturn")]).Passed);
    }

    [Fact]
    public async Task RecreatedLinksUseSemanticHash()
    {
        var beforeRaw = Raw("demand"); beforeRaw["returns"] = new JsonArray(Reference("salesreturn", Id));
        var afterRaw = Raw("demand", agent: Main); afterRaw["returns"] = new JsonArray(Reference("salesreturn", OtherId));
        var before = await Document(beforeRaw, "demand", (_, _, _) => Task.FromResult("same-semantic-hash"));
        var after = await Document(afterRaw, "demand", (_, _, _) => Task.FromResult("same-semantic-hash"));
        Assert.True(Compare("demand", [before], [after]).Passed);
        Assert.DoesNotContain(Id.ToString(), before.Data.ToString());
    }

    [Fact]
    public async Task ExistingMainDocumentsAreIncluded()
    {
        var main = await Document(Raw(agent: Main));
        var duplicate = await Document(Raw(id: OtherId));
        var result = Compare("customerorder", [main, duplicate], [main, await Document(Raw(id: OtherId, agent: Main))]);
        Assert.Equal(2, result.Count(VerificationStatus.Matched));
        Assert.Equal(0, result.Count(VerificationStatus.Unexpected));
    }

    [Fact]
    public void UnsupportedPreventsFullPassEvenWithNoKnownDocuments()
    {
        var report = DocumentComparisonService.Compare(Snapshot(), Snapshot());
        Assert.False(report.Passed); Assert.Equal(1, report.ExitCode);
        Assert.Equal(3, report.Totals[VerificationStatus.Unsupported]);
    }

    [Fact]
    public async Task UnknownArrayOrderRemainsSignificant()
    {
        var first = Raw(); first["unknownList"] = new JsonArray(1, 2);
        var second = Raw(agent: Main); second["unknownList"] = new JsonArray(2, 1);
        Assert.False(Compare("customerorder", [await Document(first)], [await Document(second)]).Passed);
    }

    [Fact]
    public async Task MixedRecreatedMultiplicityCannotBeHiddenByEqualTotalCount()
    {
        var a = await Document(Raw("salesreturn"), "salesreturn");
        var rawB = Raw("salesreturn"); rawB["name"] = "different";
        var b = await Document(rawB, "salesreturn");
        var result = Compare("salesreturn", [a, a, b],
            [a with { SourceCounterpartyId = Main }, b with { SourceCounterpartyId = Main }, b with { SourceCounterpartyId = Main }]);
        Assert.Equal(2, result.Count(VerificationStatus.Matched));
        Assert.Equal(1, result.Count(VerificationStatus.Missing));
        Assert.Equal(1, result.Count(VerificationStatus.Unexpected));
    }

    [Theory]
    [InlineData("organization")]
    [InlineData("store")]
    [InlineData("project")]
    [InlineData("contract")]
    [InlineData("agentAccount")]
    [InlineData("state")]
    public async Task BusinessReferenceChangesAreNotHiddenByAgentTransfer(string field)
    {
        var before = Raw(); before[field] = Reference(field, Id);
        var after = Raw(agent: Main); after[field] = Reference(field, OtherId);
        var result = Compare("customerorder", [await Document(before)], [await Document(after)]);
        Assert.Equal(1, result.Count(VerificationStatus.Changed));
    }
}
