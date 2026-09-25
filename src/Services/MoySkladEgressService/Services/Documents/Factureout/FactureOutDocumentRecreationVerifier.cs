using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public sealed record FactureOutDocumentRecreationVerificationResult(
    IReadOnlyDictionary<Guid, Guid> CreatedDocumentIds,
    IReadOnlyList<FactureOutPayload> MissingDocuments);

public interface IFactureOutDocumentRecreationVerifier
{
    FactureOutDocumentRecreationVerificationResult Verify(
        IReadOnlyList<FactureOutPayload> expectedDocuments,
        IReadOnlyList<MoySkladFactureOutBatchCreateResult> results);
}

public sealed class FactureOutDocumentRecreationVerifier : IFactureOutDocumentRecreationVerifier
{
    public FactureOutDocumentRecreationVerificationResult Verify(
        IReadOnlyList<FactureOutPayload> expectedDocuments,
        IReadOnlyList<MoySkladFactureOutBatchCreateResult> results)
    {
        var successfulBySyncId = results
            .Where(IsSuccessful)
            .GroupBy(result => result.ReturnedSyncId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var created = new Dictionary<Guid, Guid>();
        var missing = new List<FactureOutPayload>();
        foreach (var expected in expectedDocuments)
        {
            if (expected.NewSyncId != Guid.Empty &&
                successfulBySyncId.TryGetValue(expected.NewSyncId, out var matches) &&
                matches.Length == 1)
            {
                created.Add(expected.SourceDocumentId, matches[0].DocumentId!.Value);
            }
            else
            {
                missing.Add(expected);
            }
        }

        return new FactureOutDocumentRecreationVerificationResult(created, missing);
    }

    private static bool IsSuccessful(MoySkladFactureOutBatchCreateResult result) =>
        result.ErrorCode is null &&
        result.Error is null &&
        result.ReturnedSyncId is not null &&
        result.ReturnedSyncId != Guid.Empty &&
        result.DocumentId is not null &&
        result.DocumentId != Guid.Empty &&
        string.Equals(result.DocumentType, "factureout", StringComparison.OrdinalIgnoreCase);
}
