using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnFactureRelationsServiceTests
{
    [Fact]
    public async Task CheckAsync_MarksDocumentWithFactureRelationsAsSkipped()
    {
        var purchaseReturnId = Guid.NewGuid();
        var repository = new RecordingRepository(Snapshot(
            purchaseReturnId,
            ["facture-in-raw"],
            ["facture-out-raw"]));
        var service = Service(repository);

        var result = await service.CheckAsync(
            Guid.NewGuid(),
            [purchaseReturnId],
            CancellationToken.None);

        var item = Assert.Single(result.Documents);
        Assert.Empty(result.ReadyForRecreationIds);
        Assert.Equal("Skipped", item.Status);
        Assert.Equal("PURCHASERETURN_FACTURE_RELATIONS_PRESENT", item.ErrorCode);
        Assert.Equal(
            "The purchasereturn has facture relations (factureIn=1, factureOut=1).",
            item.Error);
    }

    [Fact]
    public async Task CheckAsync_AllowsDocumentWithoutFactureRelations()
    {
        var purchaseReturnId = Guid.NewGuid();
        var service = Service(new RecordingRepository(Snapshot(purchaseReturnId, [], [])));

        var result = await service.CheckAsync(
            Guid.NewGuid(),
            [purchaseReturnId],
            CancellationToken.None);

        var item = Assert.Single(result.Documents);
        Assert.Equal("NoRelations", item.Status);
        Assert.Equal([purchaseReturnId], result.ReadyForRecreationIds);
        Assert.Null(item.ErrorCode);
        Assert.Null(item.Error);
    }

    [Fact]
    public async Task CheckAsync_ReportsCountsForMultipleFactureRelations()
    {
        var purchaseReturnId = Guid.NewGuid();
        var service = Service(new RecordingRepository(Snapshot(
            purchaseReturnId,
            ["facture-in-1", "facture-in-2"],
            ["facture-out-1", "facture-out-2", "facture-out-3"])));

        var result = await service.CheckAsync(
            Guid.NewGuid(),
            [purchaseReturnId],
            CancellationToken.None);

        Assert.Equal(
            "The purchasereturn has facture relations (factureIn=2, factureOut=3).",
            Assert.Single(result.Documents).Error);
    }

    [Fact]
    public async Task CheckAsync_ReturnsFailureForEveryDocumentWhenSnapshotLoadingFails()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var service = Service(new FailingRepository());

        var result = await service.CheckAsync(
            Guid.NewGuid(),
            [firstId, secondId],
            CancellationToken.None);

        Assert.Equal([firstId, secondId], result.Documents.Select(item => item.PurchaseReturnId));
        Assert.All(result.Documents, item =>
        {
            Assert.Equal("Skipped", item.Status);
            Assert.Equal("PURCHASERETURN_FACTURE_SNAPSHOT_LOAD_FAILED", item.ErrorCode);
        });
    }

    private static PurchaseReturnFactureRelationsService Service(
        IPurchaseReturnFactureRelationsRepository repository) =>
        new(repository, NullLogger<PurchaseReturnFactureRelationsService>.Instance);

    private static PurchaseReturnFactureRelationsSnapshot Snapshot(
        Guid purchaseReturnId,
        IReadOnlyList<string> factureIn,
        IReadOnlyList<string> factureOut) =>
        new(purchaseReturnId, factureIn, factureOut);

    private sealed class RecordingRepository(
        params PurchaseReturnFactureRelationsSnapshot[] snapshots)
        : IPurchaseReturnFactureRelationsRepository
    {
        private readonly IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot> _snapshots =
            snapshots.ToDictionary(item => item.PurchaseReturnId);

        public Task<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>> GetSnapshotsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>>(
                purchaseReturnIds
                    .Where(id => _snapshots.ContainsKey(id))
                    .ToDictionary(id => id, id => _snapshots[id]));
    }

    private sealed class FailingRepository : IPurchaseReturnFactureRelationsRepository
    {
        public Task<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>> GetSnapshotsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromException<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>>(
                new InvalidOperationException("snapshot database is unavailable"));
    }
}
