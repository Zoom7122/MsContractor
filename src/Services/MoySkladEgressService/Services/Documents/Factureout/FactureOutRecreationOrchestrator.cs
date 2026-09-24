using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.MoySkladEgressService.Services.Documents.Factureout;

public interface IFactureOutRecreationOrchestrator
{
    Task<FactureOutRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken);
}

public sealed class FactureOutRecreationOrchestrator : IFactureOutRecreationOrchestrator
{
    public Task<FactureOutRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new FactureOutRecreationResult(
            [],
            [],
            [],
            []));
    }
}
