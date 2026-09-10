using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Services;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Counterparties;

public interface IMergeCounterpartyArchiveService
{
    Task ArchiveAsync(MergeJob job, IReadOnlyList<MergeOperation> operations, CancellationToken cancellationToken);
}

public sealed class MergeCounterpartyArchiveService(
    IMergeEgressClient egressClient,
    ICounterpartyRepository counterparties,
    IMoySkladCounterpartyParser parser,
    ICounterpartyNormalizer normalizer,
    TimeProvider timeProvider) : IMergeCounterpartyArchiveService
{
    public async Task ArchiveAsync(MergeJob job, IReadOnlyList<MergeOperation> operations, CancellationToken cancellationToken)
    {
        var response = await egressClient.ArchiveAsync(job.AccountId, operations.Select(item => item.CounterpartyId).ToArray(),
            job.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
        var parsedById = ParseArchiveBatch(response.Json, operations);
        foreach (var operation in operations)
        {
            var local = await FindLocalAsync(job.AccountId, operation.CounterpartyId, cancellationToken);
            normalizer.Apply(local, parsedById[operation.CounterpartyId], timeProvider.GetUtcNow());
        }
    }

    private IReadOnlyDictionary<Guid, ParsedCounterparty> ParseArchiveBatch(string json, IReadOnlyList<MergeOperation> operations)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("MoySklad batch archive response must be an array.");

        var expectedIds = operations.Select(item => item.CounterpartyId).ToHashSet();
        var parsedById = new Dictionary<Guid, ParsedCounterparty>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var parsed = parser.ParseOne(item.GetRawText());
            EnsureResponse(parsed.Value.Id, parsed);
            if (!expectedIds.Contains(parsed.Value.Id) || !parsedById.TryAdd(parsed.Value.Id, parsed))
                throw new JsonException("MoySklad batch archive response does not match requested counterparties.");
        }

        if (parsedById.Count != expectedIds.Count)
            throw new JsonException("MoySklad batch archive response is incomplete.");
        return parsedById;
    }

    private async Task<Counterparty> FindLocalAsync(Guid accountId, Guid counterpartyId, CancellationToken cancellationToken) =>
        await counterparties.FindTrackedAsync(accountId, counterpartyId, cancellationToken)
        ?? throw new MergeEgressException("LOCAL_COUNTERPARTY_NOT_FOUND", "Local counterparty was not found.", 500);

    private static void EnsureResponse(Guid expectedId, ParsedCounterparty parsed)
    {
        if (parsed.Value.Id != expectedId || !parsed.Value.Archived)
            throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "Egress returned an inconsistent counterparty.", 502);
    }
}
