using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnVerifierTests
{
    [Fact]
    public async Task VerifyAsync_ReadsOldSnapshotsAndNewEgressData_AndIgnoresIdsAndOrder()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var selectedContractId = Guid.NewGuid();
        var selectedAccountId = Guid.NewGuid();
        var oldPositions = new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = PositionJson(Guid.NewGuid(), 1),
            [Guid.NewGuid()] = PositionJson(Guid.NewGuid(), 2),
        };
        var newPositions = oldPositions.Values
            .Select((raw, index) => (Guid.NewGuid(), raw))
            .Reverse()
            .ToDictionary(item => item.Item1, item => item.Item2);
        var old = DocumentJson(mainCounterpartyId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), syncId, payments: true, files: true);
        var recreated = DocumentJson(mainCounterpartyId, selectedContractId, selectedAccountId, Guid.NewGuid(), syncId, payments: false, files: false)
            .Replace("\"extra\":\"ignored\"", "\"extra\":\"different\"", StringComparison.Ordinal);
        var repository = new FakeRepository(
            new Dictionary<Guid, string> { [sourceId] = old },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = oldPositions });
        var gateway = new FakePurchaseReturnGateway(
            new Dictionary<Guid, string> { [newId] = recreated });
        var positions = new FakePositionsGateway(
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [newId] = newPositions });
        var verifier = new PurchaseReturnVerifier(
            repository,
            gateway,
            positions,
            NullLogger<PurchaseReturnVerifier>.Instance);

        var result = await verifier.VerifyAsync(
            accountId,
            mainCounterpartyId,
            [new PurchaseReturnVerificationInput(sourceId, newId, syncId, selectedContractId, selectedAccountId)],
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("NeedsManualReview", document.Status);
        Assert.Empty(document.FieldMismatches);
        Assert.Empty(document.PositionMismatches);
        Assert.Single(document.Warnings);
        Assert.Contains("files", Assert.Single(document.Warnings));
        Assert.True(repository.DocumentsRead);
        Assert.True(repository.PositionsRead);
        Assert.True(gateway.DocumentsRead);
        Assert.Contains(newId, positions.Requests);
    }

    [Fact]
    public async Task VerifyAsync_ReturnsMismatchForChangedFieldAndMissingOrExtraPosition()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var expectedPositionId = Guid.NewGuid();
        var oldPositions = new Dictionary<Guid, string>
        {
            [expectedPositionId] = PositionJson(Guid.NewGuid(), 1),
        };
        var actualPositions = new Dictionary<Guid, string>
        {
            [Guid.NewGuid()] = PositionJson(Guid.NewGuid(), 3),
        };
        var repository = new FakeRepository(
            new Dictionary<Guid, string> { [sourceId] = DocumentJson(mainCounterpartyId, null, null, Guid.NewGuid(), syncId, payments: false, files: false) },
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [sourceId] = oldPositions });
        var gateway = new FakePurchaseReturnGateway(
            new Dictionary<Guid, string>
            {
                [newId] = DocumentJson(mainCounterpartyId, null, null, Guid.NewGuid(), syncId, payments: false, files: false)
                    .Replace("\"name\":\"return\"", "\"name\":\"changed\"", StringComparison.Ordinal)
            });
        var positions = new FakePositionsGateway(
            new Dictionary<Guid, IReadOnlyDictionary<Guid, string>> { [newId] = actualPositions });
        var verifier = new PurchaseReturnVerifier(
            repository,
            gateway,
            positions,
            NullLogger<PurchaseReturnVerifier>.Instance);

        var result = await verifier.VerifyAsync(
            Guid.NewGuid(),
            mainCounterpartyId,
            [new PurchaseReturnVerificationInput(sourceId, newId, syncId, null, null)],
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("Mismatch", document.Status);
        Assert.Contains(document.FieldMismatches, item => item.Field == "name");
        Assert.Contains(document.PositionMismatches, item => item.Kind == "Missing");
        Assert.Contains(document.PositionMismatches, item => item.Kind == "Extra");
    }

    private static string DocumentJson(
        Guid mainCounterpartyId,
        Guid? contractId,
        Guid? accountId,
        Guid ignoredId,
        Guid syncId,
        bool payments,
        bool files)
    {
        var contract = contractId is null ? "" : $",\"contract\":{Reference(contractId.Value, "contract")}";
        var agentAccount = accountId is null ? "" : $",\"agentAccount\":{Reference(accountId.Value, "account")}";
        return $"{{\"organization\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000001"), "organization")},\"organizationAccount\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000002"), "account")},\"store\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000003"), "store")},\"moment\":\"2026-09-20 10:00:00\",\"applicable\":true,\"shared\":true,\"name\":\"return\",\"code\":\"R-1\",\"externalCode\":\"EXT-1\",\"description\":\"description\",\"project\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000004"), "project")},\"state\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000005"), "state")},\"vatEnabled\":true,\"vatIncluded\":false,\"rate\":{{\"value\":1,\"currency\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000006"), "currency")}}},\"owner\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000007"), "employee")},\"group\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000008"), "group")},\"attributes\":[{{\"meta\":{{\"href\":\"https://old/entity/attribute/00000000-0000-0000-0000-000000000009\",\"type\":\"attributemetadata\",\"extra\":\"ignored\"}},\"value\":\"value\"}}],\"supply\":{Reference(Guid.Parse("00000000-0000-0000-0000-000000000010"), "supply")},\"agent\":{Reference(mainCounterpartyId, "counterparty")}{contract}{agentAccount},\"syncId\":\"{syncId:D}\",\"meta\":{{\"different\":true}},\"id\":\"{ignoredId:D}\",\"sum\":999,\"factureIn\":{Reference(Guid.NewGuid(), "demand")},\"factureOut\":{Reference(Guid.NewGuid(), "paymentout")}{(payments ? ",\"payments\":[{\"id\":\"old-payment\"}]" : "")}{(files ? ",\"files\":[{\"id\":\"old-file\"}]" : "")}}}";
    }

    private static string PositionJson(Guid assortmentId, int quantity) =>
        $"{{\"id\":\"{Guid.NewGuid():D}\",\"meta\":{{\"extra\":true}},\"accountId\":\"{Guid.NewGuid():D}\",\"assortment\":{Reference(assortmentId, "product")},\"quantity\":{quantity},\"price\":100,\"discount\":0,\"vat\":\"20\",\"vatEnabled\":true,\"pack\":{Reference(Guid.NewGuid(), "uom")},\"slot\":{Reference(Guid.NewGuid(), "slot")},\"things\":[]}}";

    private static string Reference(Guid id, string type) =>
        $"{{\"meta\":{{\"href\":\"https://old/entity/{type}/{id:D}\",\"type\":\"{type}\",\"mediaType\":\"application/json\"}}}}";

    private sealed class FakeRepository(
        IReadOnlyDictionary<Guid, string> documents,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> positions) : IPurchaseReturnPreparationRepository
    {
        public bool DocumentsRead { get; private set; }
        public bool PositionsRead { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredDocumentsAsync(Guid accountId, IReadOnlyCollection<Guid> purchaseReturnIds, CancellationToken cancellationToken)
        {
            DocumentsRead = true;
            return Task.FromResult(documents);
        }

        public Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>>> GetRequiredPositionsAsync(Guid accountId, IReadOnlyCollection<Guid> purchaseReturnIds, CancellationToken cancellationToken)
        {
            PositionsRead = true;
            return Task.FromResult(positions);
        }

        public Task UpsertDocumentsAsync(Guid accountId, IReadOnlyDictionary<Guid, string> documents, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReplacePositionsAsync(Guid accountId, Guid purchaseReturnId, IReadOnlyDictionary<Guid, string> positions, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePurchaseReturnGateway(IReadOnlyDictionary<Guid, string> documents) : IMoySkladPurchaseReturnGateway
    {
        public bool DocumentsRead { get; private set; }

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(Guid accountId, Guid requestedByUserId, string correlationId, IReadOnlyList<Guid> purchaseReturnIds, CancellationToken cancellationToken)
        {
            DocumentsRead = true;
            return Task.FromResult(documents);
        }

        public Task<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>> GetAgentAccountsAsync(Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnAgentAccount>>([]);
        public Task<IReadOnlyList<MoySkladPurchaseReturnContract>> GetContractsAsync(Guid accountId, Guid mainCounterpartyId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MoySkladPurchaseReturnContract>>([]);
        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchDeleteResult>> DeleteBatchAsync(Guid accountId, string correlationId, IReadOnlyList<Guid> purchaseReturnIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<MoySkladPurchaseReturnBatchCreateResult>> CreateBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladPurchaseReturnBatchCreateItem> documents, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePositionsGateway(
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string>> positions) : IMoySkladPurchaseReturnPositionsGateway
    {
        public List<Guid> Requests { get; } = [];

        public Task<MoySkladPurchaseReturnPositionsPage> GetPageAsync(Guid accountId, Guid requestedByUserId, string correlationId, Guid purchaseReturnId, int limit, int offset, CancellationToken cancellationToken)
        {
            Requests.Add(purchaseReturnId);
            var rows = positions[purchaseReturnId];
            return Task.FromResult(new MoySkladPurchaseReturnPositionsPage(rows.Count, limit, offset, rows));
        }
    }
}
