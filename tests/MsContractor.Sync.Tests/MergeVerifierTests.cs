using MergeVerifier.Models;
using MergeVerifier.Normalization;
using MergeVerifier.Verification;

namespace MsContractor.Sync.Tests;

public sealed class MergeVerifierTests
{
    private static readonly Guid Account = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Main = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Duplicate = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Id = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void Reassigned_same_id_and_main_agent_is_matched() =>
        Assert.Equal("Matched", Verify(Doc("customerorder", Id, Duplicate), Doc("customerorder", Id, Main)).Documents.Single().Status);

    [Fact]
    public void Reassigned_wrong_agent_is_changed() =>
        Assert.Equal("Changed", Verify(Doc("customerorder", Id, Duplicate), Doc("customerorder", Id, Duplicate)).Documents.Single().Status);

    [Fact]
    public void Missing_reassigned_document_is_reported() =>
        Assert.Equal("Missing", Verify(Doc("customerorder", Id, Duplicate)).Documents.Single(item => item.BeforeDocumentId == Id).Status);

    [Fact]
    public void Recreated_new_id_with_same_normalized_content_is_matched()
    {
        var next = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var result = Verify(Doc("salesreturn", Id, Duplicate), Doc("salesreturn", next, Main));
        Assert.Equal("Matched", result.Documents.Single(item => item.BeforeDocumentId == Id).Status);
    }

    [Fact]
    public void Recreated_changed_position_is_changed()
    {
        var next = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var result = Verify(Doc("salesreturn", Id, Duplicate), Doc("salesreturn", next, Main, positionPrice: 20));
        Assert.Equal("Changed", result.Documents.Single(item => item.BeforeDocumentId == Id).Status);
    }

    [Fact]
    public void Recreated_multiple_equal_candidates_is_ambiguous()
    {
        var first = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var second = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var result = Verify(Doc("salesreturn", Id, Duplicate), Doc("salesreturn", first, Main), Doc("salesreturn", second, Main));
        Assert.Equal("Ambiguous", result.Documents.Single(item => item.BeforeDocumentId == Id).Status);
    }

    [Fact]
    public void Unmapped_after_document_is_unexpected()
    {
        var next = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var result = Verify(Doc("customerorder", Id, Duplicate), Doc("customerorder", Id, Main), Doc("customerorder", next, Main));
        Assert.Equal(1, result.Unexpected);
    }

    [Fact]
    public void Canonicalization_sorts_properties_and_normalizes_reference_href()
    {
        var first = JsonCanonicalizer.Canonicalize(System.Text.Json.Nodes.JsonNode.Parse("{\"b\":1,\"a\":{\"href\":\"https://x/entity/product/44444444-4444-4444-4444-444444444444\",\"type\":\"product\"}}")!);
        var second = JsonCanonicalizer.Canonicalize(System.Text.Json.Nodes.JsonNode.Parse("{\"a\":{\"type\":\"product\",\"href\":\"https://y/entity/product/44444444-4444-4444-4444-444444444444\"},\"b\":1}")!);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Normalized_hash_is_stable()
    {
        var first = DocumentNormalizer.Normalize("salesreturn", Doc("salesreturn", Id, Duplicate).RawJson, Doc("salesreturn", Id, Duplicate).PositionRawJson, true).Hash;
        var second = DocumentNormalizer.Normalize("salesreturn", Doc("salesreturn", Id, Duplicate).RawJson, Doc("salesreturn", Id, Duplicate).PositionRawJson, true).Hash;
        Assert.Equal(first, second);
    }

    [Fact]
    public void Main_and_duplicate_statuses_are_verified()
    {
        var before = Snapshot([Counterparty(Main, false), Counterparty(Duplicate, false)], []);
        var after = Snapshot([Counterparty(Main, false), Counterparty(Duplicate, true)], []);
        var report = new MergeVerificationService().Verify(before, after);
        Assert.True(report.Counterparties.MainExists);
        Assert.False(report.Counterparties.MainArchived);
        Assert.True(report.Counterparties.Duplicates.Single().Passed);
    }

    [Fact]
    public void Unarchived_duplicate_fails_verification()
    {
        var before = Snapshot([Counterparty(Main, false), Counterparty(Duplicate, false)], []);
        var after = Snapshot([Counterparty(Main, false), Counterparty(Duplicate, false)], []);
        Assert.False(new MergeVerificationService().Verify(before, after).Passed);
    }

    private static VerificationReport Verify(DocumentSnapshot before, params DocumentSnapshot[] after) =>
        new MergeVerificationService().Verify(Snapshot([Counterparty(Main, false), Counterparty(Duplicate, false)], [before]),
            Snapshot([Counterparty(Main, false), Counterparty(Duplicate, true)], after));
    private static MergeSnapshot Snapshot(IReadOnlyList<CounterpartySnapshot> counterparties, IReadOnlyList<DocumentSnapshot> documents) =>
        new("1", Account, Main, [Duplicate], DateTimeOffset.UtcNow, counterparties, documents, ["customerorder", "salesreturn"]);
    private static CounterpartySnapshot Counterparty(Guid id, bool archived)
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(new { id, archived, name = "x" });
        return new CounterpartySnapshot(id, id == Main ? "Main" : "Duplicate", archived, raw, raw);
    }
    private static DocumentSnapshot Doc(string type, Guid id, Guid agent, decimal positionPrice = 10)
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            id, name = "#1", externalCode = "ext", moment = "2026-01-01 10:00:00", sum = 10,
            agent = new { meta = new { href = $"https://example.test/api/remap/1.2/entity/counterparty/{agent:D}", type = "counterparty" } },
            organization = new { meta = new { href = "https://example.test/api/remap/1.2/entity/organization/77777777-7777-7777-7777-777777777777", type = "organization" } }
        });
        var positions = new[] { System.Text.Json.JsonSerializer.Serialize(new
        {
            assortment = new { meta = new { href = "https://example.test/entity/product/88888888-8888-8888-8888-888888888888", type = "product" } },
            quantity = 1, price = positionPrice
        }) };
        var normalized = DocumentNormalizer.Normalize(type, raw, positions, type == "salesreturn");
        return new DocumentSnapshot(type, id, agent, "#1", "ext", "2026-01-01 10:00:00", 10, agent, positions, raw, normalized.Json, normalized.Hash);
    }
}
