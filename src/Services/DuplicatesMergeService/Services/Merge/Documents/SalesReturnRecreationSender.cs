using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface ISalesReturnRecreationSender
{
    Task<SalesReturnRecreationResponse> SendAsync(
        Guid accountId,
        Guid mainAgentId,
        string documentType,
        IReadOnlyList<Guid> documentIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRecreationSender(
    ISalesReturnRecreationEgressClient client) : ISalesReturnRecreationSender
{
    public Task<SalesReturnRecreationResponse> SendAsync(
        Guid accountId,
        Guid mainAgentId,
        string documentType,
        IReadOnlyList<Guid> documentIds,
        Guid mergeJobId,
        Guid operationId,
        Guid userId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(documentType, "salesreturn", StringComparison.Ordinal))
            throw new ArgumentException("The salesreturn sender supports only salesreturn documents.", nameof(documentType));

        return client.RecreateAsync(
            accountId,
            mainAgentId,
            documentIds,
            mergeJobId,
            operationId,
            userId,
            correlationId,
            cancellationToken);
    }
}
