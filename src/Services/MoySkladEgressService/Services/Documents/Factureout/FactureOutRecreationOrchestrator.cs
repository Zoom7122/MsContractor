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
    private readonly IFactureOutPreparationService _preparationService;

    public FactureOutRecreationOrchestrator(IFactureOutPreparationService preparationService)
    {
        _preparationService = preparationService;
    }

    public async Task<FactureOutRecreationResult> ExecuteAsync(
        Guid accountId,
        Guid mainCounterpartyId,
        IReadOnlyList<Guid> factureOutIds,
        CancellationToken cancellationToken)
    {
        var preparation = await _preparationService.PrepareAsync(
            accountId,
            factureOutIds,
            cancellationToken);

        return new FactureOutRecreationResult(
            [],
            preparation.SkippedDocuments,
            [],
            []);
    }
}
