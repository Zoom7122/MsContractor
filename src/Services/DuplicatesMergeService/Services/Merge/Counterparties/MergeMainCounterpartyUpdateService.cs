using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Counterparties;

public interface IMergeMainCounterpartyUpdateService
{
    Task UpdateAsync(MergeJob job, MergeOperation operation, MergeMainCounterpartyDto snapshot,
        CancellationToken cancellationToken);
}

public sealed class MergeMainCounterpartyUpdateService(
    IMergeEgressClient egressClient,
    ICounterpartyRepository counterparties,
    IMoySkladCounterpartyParser parser,
    ICounterpartyNormalizer normalizer,
    TimeProvider timeProvider) : IMergeMainCounterpartyUpdateService
{
    public async Task UpdateAsync(MergeJob job, MergeOperation operation, MergeMainCounterpartyDto snapshot,
        CancellationToken cancellationToken)
    {
        var response = await egressClient.UpdateAsync(job.AccountId, operation.CounterpartyId, snapshot, job.Id,
            operation.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
        var parsed = parser.ParseOne(response.Json);
        EnsureResponse(operation.CounterpartyId, parsed, archivedRequired: false);
        var local = await FindLocalAsync(job.AccountId, operation.CounterpartyId, cancellationToken);
        normalizer.Apply(local, parsed, timeProvider.GetUtcNow());
    }

    private async Task<Counterparty> FindLocalAsync(Guid accountId, Guid counterpartyId, CancellationToken cancellationToken) =>
        await counterparties.FindTrackedAsync(accountId, counterpartyId, cancellationToken)
        ?? throw new MergeEgressException("LOCAL_COUNTERPARTY_NOT_FOUND", "Local counterparty was not found.", 500);

    private static void EnsureResponse(Guid expectedId, ParsedCounterparty parsed, bool archivedRequired)
    {
        if (parsed.Value.Id != expectedId || (archivedRequired && !parsed.Value.Archived))
            throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "Egress returned an inconsistent counterparty.", 502);
    }
}
