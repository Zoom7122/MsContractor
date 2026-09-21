using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface IPurchaseReturnRecreationSender
{
    Task<PurchaseReturnRecreationResponse> SendAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<Guid> documentIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class PurchaseReturnRecreationSender(
    IPurchaseReturnRecreationEgressClient client) : IPurchaseReturnRecreationSender
{
    public Task<PurchaseReturnRecreationResponse> SendAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        string documentType,
        IReadOnlyList<Guid> documentIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(documentType, "purchasereturn", StringComparison.Ordinal))
            throw new ArgumentException("The purchasereturn sender supports only purchasereturn documents.", nameof(documentType));

        return client.RecreateAsync(
            accountId,
            mainCounterpartyId,
            documentIds,
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);
    }
}
