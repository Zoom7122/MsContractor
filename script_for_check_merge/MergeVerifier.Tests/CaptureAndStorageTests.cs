using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MergeVerifier.Capture;
using MergeVerifier.Cli;
using MergeVerifier.Comparison;
using MergeVerifier.Documents;
using MergeVerifier.Models;
using MergeVerifier.MoySklad;
using MergeVerifier.Storage;
using Xunit;
using static MergeVerifier.Tests.Fixtures;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MergeVerifier.Tests;

// In-memory representation of the external API, with the actual HTTP client and pagination reader.
internal sealed class AccountApi
{
    public List<JsonObject> Documents { get; } = [];
    public List<string> Paths { get; } = [];
    public bool FailPositions { get; set; }
    public MoySkladClient CreateClient() => new(BaseUrl, "fixture-login", "fixture-password", new StubHandler(Respond), (_, _) => Task.CompletedTask);

    private HttpResponseMessage Respond(HttpRequestMessage request, int call)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        var uri = request.RequestUri!;
        var path = uri.AbsolutePath.Split("/entity/")[1];
        Paths.Add(path);
        Assert.DoesNotContain("counterparty/", path, StringComparison.Ordinal);
        var segments = path.Split('/');
        var type = segments[0];
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
        JsonObject[] rows;
        if (segments.Length == 1)
        {
            Assert.True(query.TryGetValue("filter", out var filter));
            Assert.StartsWith("agent=https://", filter, StringComparison.Ordinal);
            Assert.Contains("filter=agent=https%3A%2F%2F", uri.Query, StringComparison.Ordinal);
            var owner = Guid.Parse(filter!.Split('/').Last());
            rows = Documents.Where(x => x["meta"]!["type"]!.GetValue<string>() == type && SnapshotCollector.ReadAgent(x) == owner).ToArray();
        }
        else
        {
            var doc = Documents.SingleOrDefault(x => x["id"]!.GetValue<string>() == segments[1] && x["meta"]!["type"]!.GetValue<string>() == type);
            if (doc is null) return new(HttpStatusCode.NotFound);
            if (segments.Length == 2)
            {
                var detail = (JsonObject)doc.DeepClone();
                foreach (var (key, value) in detail.ToArray())
                    if (value is JsonArray array && (DocumentRegistry.Get(type).PositionCollections.Contains(key) || key == "files"))
                        detail[key] = new JsonObject { ["meta"] = new JsonObject
                        { ["href"] = new Uri(BaseUrl, $"entity/{path}/{key.ToLowerInvariant()}").AbsoluteUri, ["size"] = array.Count } };
                return StubHandler.Json(detail);
            }
            if (FailPositions) return new(HttpStatusCode.BadGateway);
            var collection = doc.First(x => x.Key.Equals(segments[2], StringComparison.OrdinalIgnoreCase)).Value!.AsArray();
            rows = collection.Select(x => x!.AsObject()).ToArray();
        }
        var offset = int.Parse(query["offset"]);
        // Force several pages for even a small fixture.
        const int limit = 1;
        var selected = rows.Skip(offset).Take(limit).ToArray();
        return StubHandler.Json(HttpTests.Page(selected, rows.Length, offset, limit));
    }
}

public sealed class CaptureAndStorageTests
{
    [Fact]
    public async Task CaptureVerifyEndToEndPreservesBeforeAndWritesReports()
    {
        var folder = Path.Combine(Path.GetTempPath(), "merge-verifier-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var api = new AccountApi();
            api.Documents.Add(Raw("customerorder", agent: Main, quantities: [1, 2]));
            api.Documents.Add(Raw("salesreturn", OtherId, Duplicate, 1, 1));
            api.Documents.Add(Raw("paymentin", Guid.NewGuid(), Duplicate));
            api.Documents.Add(Raw("commissionreportin", Guid.NewGuid(), Duplicate, 1, 2));
            using var client = api.CreateClient();
            var output = new StringWriter(); var error = new StringWriter();
            var collector = new SnapshotCollector(client, output);
            var before = await collector.CaptureAsync(Main, [Duplicate], default);
            SnapshotStore.Validate(before);
            Assert.Equal(4, before.Documents.Count);
            Assert.Equal(2, before.Documents.Single(x => x.EntityType == "salesreturn").Data.GetProperty("positions").GetArrayLength());
            Assert.DoesNotContain(api.Paths, x => x.StartsWith("paymentin/") && x.EndsWith("/positions"));
            Assert.Contains(api.Paths, x => x.EndsWith("/returntocommissionerpositions"));
            Assert.DoesNotContain(api.Paths, x => x.StartsWith("retireorder"));
            var path = Path.Combine(folder, "before.json");
            await SnapshotStore.WriteAsync(path, before, false);
            var bytes = await File.ReadAllBytesAsync(path);
            foreach (var document in api.Documents)
            {
                document["agent"] = Reference("counterparty", Main);
                if (document["meta"]!["type"]!.GetValue<string>() == "salesreturn")
                {
                    var replacement = Guid.NewGuid();
                    document["id"] = replacement.ToString();
                    document["meta"] = Reference("salesreturn", replacement)["meta"]!.DeepClone();
                    document["href"] = "technical-new-href";
                    document["uuidHref"] = "technical-new-uuid-href";
                    document["created"] = "later";
                }
            }
            var result = await VerifierApplication.VerifyAsync(await SnapshotStore.ReadAsync(path), path, collector, output, error);
            Assert.Equal(1, result); // All four documents match; three registry types remain explicitly Unsupported.
            Assert.Empty(error.ToString());
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.True(File.Exists(Path.Combine(folder, "after.json")));
            var report = JsonSerializer.Deserialize<VerificationReport>(await File.ReadAllTextAsync(Path.Combine(folder, "report.json")), SnapshotStore.JsonOptions)!;
            Assert.Equal(4, report.Totals[VerificationStatus.Matched]);
            Assert.Equal(3, report.Totals[VerificationStatus.Unsupported]);
            foreach (var text in new[] { output.ToString(), JsonSerializer.Serialize(before), JsonSerializer.Serialize(report) })
            {
                Assert.DoesNotContain("fixture-login", text); Assert.DoesNotContain("fixture-password", text);
                Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture-login:fixture-password")), text);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FailedPositionPageProducesErrorWithoutPartialAfter()
    {
        var folder = Path.Combine(Path.GetTempPath(), "merge-verifier-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var api = new AccountApi(); api.Documents.Add(Raw()); api.FailPositions = true;
            using var client = api.CreateClient();
            var before = Snapshot(await Document(Raw()));
            var path = Path.Combine(folder, "before.json");
            await SnapshotStore.WriteAsync(path, before, false);
            var result = await VerifierApplication.VerifyAsync(before, path, new(client, TextWriter.Null), TextWriter.Null, TextWriter.Null);
            Assert.Equal(2, result);
            Assert.False(File.Exists(Path.Combine(folder, "after.json")));
            var report = await File.ReadAllTextAsync(Path.Combine(folder, "report.json"));
            Assert.Contains("FetchError", report);
            Assert.Contains("\"afterCaptureCompleted\": false", report);
            Assert.DoesNotContain("fixture-password", report);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task RecreatedReferenceCanBeResolvedOutsideSelectedScope()
    {
        var api = new AccountApi();
        var demand = Raw("demand"); demand["returns"] = new JsonArray(Reference("salesreturn", OtherId));
        var related = Raw("salesreturn", OtherId, Guid.NewGuid());
        api.Documents.Add(demand); api.Documents.Add(related);
        using var client = api.CreateClient();
        var snapshot = await new SnapshotCollector(client, TextWriter.Null).CaptureAsync(Main, [Duplicate], default);
        Assert.Single(snapshot.Documents);
        var data = snapshot.Documents[0].Data.ToString();
        Assert.Contains("semanticHash", data);
        Assert.DoesNotContain("salesreturn/" + OtherId, data);
        Assert.Contains(api.Paths, x => x == "salesreturn/" + OtherId);
    }

    [Fact]
    public async Task CyclicRecreatedReferencesFailExplicitly()
    {
        var api = new AccountApi();
        var doc = Raw("salesreturn"); doc["related"] = Reference("salesreturn", Id); api.Documents.Add(doc);
        using var client = api.CreateClient();
        var exception = await Assert.ThrowsAsync<VerifierException>(() => new SnapshotCollector(client, TextWriter.Null).CaptureAsync(Main, [Duplicate], default));
        Assert.Contains("Cyclic", exception.Message);
        Assert.DoesNotContain(Id.ToString(), exception.Message);
    }

    [Fact]
    public async Task UnexplainedSelfIdIsRejectedBeforePersistence()
    {
        var api = new AccountApi();
        var doc = Raw("salesreturn"); doc["newTechnicalLink"] = "self/" + Id; api.Documents.Add(doc);
        using var client = api.CreateClient();
        var exception = await Assert.ThrowsAsync<VerifierException>(() => new SnapshotCollector(client, TextWriter.Null).CaptureAsync(Main, [Duplicate], default));
        Assert.Contains("ID remains", exception.Message);
        Assert.DoesNotContain(Id.ToString(), exception.Message);
    }

    [Fact]
    public async Task FilesAreFullyLoadedWithStableFileIdentityAndDuplicates()
    {
        var api = new AccountApi(); var doc = Raw("salesreturn");
        var fileId = Guid.NewGuid();
        var file = new JsonObject
        {
            ["id"] = fileId.ToString(), ["filename"] = "invoice.pdf", ["size"] = 23,
            ["meta"] = new JsonObject { ["href"] = new Uri(BaseUrl, $"entity/salesreturn/{Id}/files/{fileId}").AbsoluteUri },
            ["created"] = "yesterday", ["tiny"] = new JsonObject { ["href"] = "temporary" }
        };
        doc["files"] = new JsonArray(file.DeepClone(), file.DeepClone()); api.Documents.Add(doc);
        using var client = api.CreateClient();
        var snapshot = await new SnapshotCollector(client, TextWriter.Null).CaptureAsync(Main, [Duplicate], default);
        var files = snapshot.Documents.Single().Data.GetProperty("files");
        Assert.Equal(2, files.GetArrayLength());
        Assert.Equal(fileId.ToString(), files[0].GetProperty("fileIdentity").GetString());
        Assert.DoesNotContain(Id.ToString(), snapshot.Documents.Single().Data.ToString());
    }

    [Fact]
    public async Task BeforeCannotBeOverwritten()
    {
        var folder = Path.Combine(Path.GetTempPath(), "merge-verifier-test-" + Guid.NewGuid());
        var path = Path.Combine(folder, "before.json");
        try
        {
            await SnapshotStore.WriteAsync(path, Snapshot(), false);
            var original = await File.ReadAllBytesAsync(path);
            await Assert.ThrowsAsync<IOException>(() => SnapshotStore.WriteAsync(path, Snapshot(), false));
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.GetFiles(folder));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("version")]
    [InlineData("coverage")]
    [InlineData("owner")]
    [InlineData("duplicate")]
    [InlineData("technical")]
    [InlineData("positions")]
    public async Task InvalidSnapshotsAreRejected(string reason)
    {
        var document = await Document(Raw());
        var snapshot = Snapshot(document);
        snapshot = reason switch
        {
            "version" => snapshot with { SnapshotVersion = 12 },
            "coverage" => snapshot with { Coverage = [] },
            "owner" => snapshot with { Documents = [document with { SourceCounterpartyId = Guid.NewGuid() }] },
            "duplicate" => snapshot with { Documents = [document, document] },
            "technical" => snapshot with { Documents = [document with { Data = JsonSerializer.SerializeToElement(Raw()) }] },
            "positions" => snapshot with { Documents = [document with { Data = JsonSerializer.SerializeToElement(new { name = "empty" }) }] },
            _ => snapshot
        };
        Assert.Throws<VerifierException>(() => SnapshotStore.Validate(snapshot));
    }

    [Theory]
    [InlineData("{\"snapshotVersion\":99}")]
    [InlineData("{}")]
    [InlineData("{\"snapshotVersion\":1,\"snapshotVersion\":1}")]
    public async Task InvalidSnapshotFileReturnsExitTwoWithoutNetwork(string json)
    {
        var folder = Path.Combine(Path.GetTempPath(), "merge-verifier-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "before.json"); await File.WriteAllTextAsync(path, json);
            Assert.Equal(2, await VerifierApplication.RunAsync(["verify", "--snapshot", path], TextWriter.Null, TextWriter.Null));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void CliDeduplicatesAndRejectsInvalidScope()
    {
        var command = CommandLine.Parse(["capture", "--main", Main.ToString(), "--duplicates", $"{Duplicate},{Duplicate}"]);
        Assert.Single(command.Duplicates);
        Assert.Throws<VerifierException>(() => CommandLine.Parse(["capture", "--main", Main.ToString(), "--duplicates", Main.ToString()]));
        Assert.Throws<VerifierException>(() => CommandLine.Parse(["capture", "--main", "invalid", "--duplicates", Duplicate.ToString()]));
        Assert.Throws<VerifierException>(() => CommandLine.Parse(["capture", "--main", Main.ToString(), "--duplicates", $"{Duplicate},"]));
        Assert.Throws<VerifierException>(() => CommandLine.Parse(["verify", "--snapshot", "after.json"]));
    }

    [Theory]
    [InlineData("MOYSKLAD_LOGIN")]
    [InlineData("MOYSKLAD_PASSWORD")]
    public async Task MissingCredentialsReturnTwoBeforeSending(string name)
    {
        var old = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, null);
            var error = new StringWriter();
            var code = await VerifierApplication.RunAsync(["capture", "--main", Main.ToString(), "--duplicates", Duplicate.ToString()], TextWriter.Null, error);
            Assert.Equal(2, code);
            Assert.Contains("MOYSKLAD_LOGIN", error.ToString());
        }
        finally { Environment.SetEnvironmentVariable(name, old); }
    }
}
