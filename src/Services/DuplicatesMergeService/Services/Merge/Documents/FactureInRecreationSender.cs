using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface IFactureInRecreationSender
{
    Task<FactureInRecreationResponse> SendAsync(
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

public sealed class FactureInRecreationSender(
    IFactureInRecreationEgressClient client) : IFactureInRecreationSender
{
    public Task<FactureInRecreationResponse> SendAsync(
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
        if (!string.Equals(documentType, "facturein", StringComparison.Ordinal))
            throw new ArgumentException("The facturein sender supports only facturein documents.", nameof(documentType));

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
