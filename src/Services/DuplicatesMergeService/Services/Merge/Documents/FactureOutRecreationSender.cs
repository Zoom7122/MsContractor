using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface IFactureOutRecreationSender
{
    Task<FactureOutRecreationResponse> SendAsync(
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

public sealed class FactureOutRecreationSender(
    IFactureOutRecreationEgressClient client) : IFactureOutRecreationSender
{
    public Task<FactureOutRecreationResponse> SendAsync(
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
        if (!string.Equals(documentType, "factureout", StringComparison.Ordinal))
            throw new ArgumentException("The factureout sender supports only factureout documents.", nameof(documentType));

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
