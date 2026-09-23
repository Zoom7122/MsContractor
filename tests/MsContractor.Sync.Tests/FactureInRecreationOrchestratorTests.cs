using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Facturein;

namespace MsContractor.Sync.Tests;

public sealed class FactureInRecreationOrchestratorTests
{
    [Fact]
    public async Task ExecuteAsync_PreparesDocumentsBeforeBuildingAndTransferring()
    {
        var accountId = Guid.NewGuid();
        var documentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var preparation = new RecordingPreparationService();
        var builder = new RecordingPayloadBuilder();
        var orchestrator = new FactureInRecreationOrchestrator(
            preparation,
            builder,
            new RecordingTransferService(),
            new RecordingItemRepository(),
            NullLogger<FactureInRecreationOrchestrator>.Instance);

        var result = await orchestrator.ExecuteAsync(
            accountId,
            Guid.NewGuid(),
            documentIds,
            CancellationToken.None);

        Assert.Equal(accountId, preparation.AccountId);
        Assert.Equal(documentIds, preparation.DocumentIds);
        Assert.Equal(documentIds, builder.DocumentIds);
        Assert.Empty(result.TransferredDocumentIds);
    }

    private sealed class RecordingPreparationService : IFactureInPreparationService
    {
        public Guid? AccountId { get; private set; }
        public IReadOnlyList<Guid>? DocumentIds { get; private set; }

        public Task<FactureInPreparationResult> PrepareAsync(
            Guid accountId,
            IReadOnlyList<Guid> factureInIds,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            DocumentIds = factureInIds;
            return Task.FromResult(new FactureInPreparationResult(
                factureInIds.ToDictionary(id => id, _ => "{}"),
                []));
        }
    }

    private sealed class RecordingPayloadBuilder : IFactureInPayloadBuilder
    {
        public IReadOnlyCollection<Guid>? DocumentIds { get; private set; }
        public Task<FactureInPayloadBuildResult> BuildAsync(Guid accountId, Guid mainCounterpartyId,
            IReadOnlyCollection<Guid> factureInIds, CancellationToken cancellationToken)
        {
            DocumentIds = factureInIds;
            return Task.FromResult(new FactureInPayloadBuildResult([], [], [], []));
        }
    }

    private sealed class RecordingTransferService : IFactureInDocumentTransferService
    {
        public Task<FactureInTransferResult> TransferAsync(Guid accountId,
            IReadOnlyCollection<FactureInRecreationItem> documents, CancellationToken cancellationToken) =>
            Task.FromResult(new FactureInTransferResult([], []));
    }

    private sealed class RecordingItemRepository : IFactureInRecreationItemRepository
    {
        public Task<IReadOnlyDictionary<Guid, FactureInRecreationItem>> GetAsync(Guid accountId,
            IReadOnlyCollection<Guid> sourceFactureInIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, FactureInRecreationItem>>(new Dictionary<Guid, FactureInRecreationItem>());
        public Task<FactureInRecreationItem> CreateOrGetAsync(FactureInRecreationItem item,
            CancellationToken cancellationToken) => Task.FromResult(item);
        public Task SaveAsync(FactureInRecreationItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
