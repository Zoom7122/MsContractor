using System.Text.Json;
using MergeVerifier.Comparison;
using MergeVerifier.Models;

namespace MergeVerifier.Verification;

public sealed class MergeVerificationService
{
    private readonly IReadOnlyList<IDocumentComparer> _comparers =
    [new SalesReturnComparer(), new ReassignedDocumentComparer()];

    public VerificationReport Verify(MergeSnapshot before, MergeSnapshot after)
    {
        if (before.AccountId != after.AccountId || before.MainCounterpartyId != after.MainCounterpartyId ||
            !before.DuplicateCounterpartyIds.Order().SequenceEqual(after.DuplicateCounterpartyIds.Order()))
            throw new InvalidOperationException("AFTER snapshot does not describe the same merge scope.");
        var counterpartyReport = VerifyCounterparties(before, after);
        var results = new List<DocumentVerification>();
        var consumed = new HashSet<(string Type, Guid Id)>();
        foreach (var document in before.Documents)
        {
            var comparer = _comparers.First(item => item.CanCompare(document.DocumentType));
            var result = comparer.Compare(document, after.Documents, before.MainCounterpartyId, out var matched);
            results.Add(result);
            if (matched is not null) consumed.Add((matched.DocumentType, matched.DocumentId));
        }
        var unexpected = after.Documents.Where(item => !consumed.Contains((item.DocumentType, item.DocumentId))).Select(item =>
            new DocumentVerification(item.DocumentType, Guid.Empty, item.DocumentId, "unexpected", "Unexpected")).ToArray();
        results.AddRange(unexpected);
        var types = before.DocumentTypes.Concat(after.DocumentTypes).Distinct(StringComparer.Ordinal).Order().Select(type =>
        {
            var source = before.Documents.Where(item => item.DocumentType == type).ToArray();
            var matching = results.Where(item => item.DocumentType == type).ToArray();
            return new DocumentTypeSummary(type, source.Count(item => item.SourceCounterpartyId == before.MainCounterpartyId),
                source.Count(item => item.SourceCounterpartyId != before.MainCounterpartyId),
                after.Documents.Count(item => item.DocumentType == type && item.AgentId == before.MainCounterpartyId),
                matching.Count(item => item.Status == "Matched"), matching.Count(item => item.Status == "Missing"),
                matching.Count(item => item.Status == "Changed"), matching.Count(item => item.Status == "Ambiguous"),
                matching.Count(item => item.Status == "Unexpected"));
        }).ToArray();
        return new VerificationReport("1", DateTimeOffset.UtcNow, before.AccountId, before.MainCounterpartyId,
            counterpartyReport, results, types, results.Count(item => item.Status == "Matched"),
            results.Count(item => item.Status == "Missing"), results.Count(item => item.Status == "Changed"),
            results.Count(item => item.Status == "Ambiguous"), results.Count(item => item.Status == "Unexpected"));
    }

    private static CounterpartyVerification VerifyCounterparties(MergeSnapshot before, MergeSnapshot after)
    {
        var mainBefore = before.Counterparties.Single(item => item.CounterpartyId == before.MainCounterpartyId);
        var mainAfter = after.Counterparties.SingleOrDefault(item => item.CounterpartyId == after.MainCounterpartyId);
        var duplicates = before.DuplicateCounterpartyIds.Select(id =>
        {
            var value = after.Counterparties.SingleOrDefault(item => item.CounterpartyId == id);
            return new DuplicateCounterpartyVerification(id, value is not null, value?.Archived, value is { Archived: true });
        }).ToArray();
        return new CounterpartyVerification(mainAfter is not null, mainAfter?.Archived, duplicates,
            mainAfter is null ? [] : DiffCounterparty(mainBefore.RawJson, mainAfter.RawJson));
    }

    private static IReadOnlyList<CounterpartyFieldChange> DiffCounterparty(string beforeJson, string afterJson)
    {
        using var before = JsonDocument.Parse(beforeJson);
        using var after = JsonDocument.Parse(afterJson);
        var names = before.RootElement.EnumerateObject().Select(item => item.Name)
            .Concat(after.RootElement.EnumerateObject().Select(item => item.Name)).Distinct(StringComparer.Ordinal)
            .Where(name => name is not "meta" and not "updated" and not "created" and not "accountId");
        return names.Select(name => (name, Before: Raw(before.RootElement, name), After: Raw(after.RootElement, name)))
            .Where(item => item.Before != item.After).Select(item => new CounterpartyFieldChange(item.name, item.Before, item.After)).ToArray();
    }
    private static string? Raw(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.GetRawText() : null;
}
