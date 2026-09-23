using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Purchasereturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnMoneyRelationsTests
{
    [Fact]
    public async Task CheckAsync_DetachesAllMatchingOperationsAndSavesOriginalSnapshot()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var gateway = new RecordingGateway();
        gateway.Documents[(MoneyType.PaymentIn, paymentId)] = MoneyDocument(
            "paymentin",
            paymentId,
            Operation("keep-1", "paymentin", Guid.NewGuid()),
            Operation("source-1", "purchasereturn", sourceId, 10m),
            Operation("source-2", "purchasereturn", sourceId, 20m),
            Operation("keep-2", "paymentin", Guid.NewGuid()));
        gateway.Documents[(MoneyType.CashIn, cashId)] = MoneyDocument(
            "cashin",
            cashId,
            Operation("source-cash", "purchasereturn", sourceId, 70000m),
            Operation("keep-cash", "cashin", Guid.NewGuid()));
        var snapshot = new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{\"meta\":{}}")],
            [new PurchaseReturnMoneyRelationSnapshot(cashId, "{\"meta\":{}}")]);
        var repository = new RecordingRepository(snapshot);

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("Detached", document.Status);
        Assert.Equal(["paymentin", "cashin"], gateway.Batches.Select(item => item.Type));
        Assert.Equal(2, repository.SavedSnapshots.Count);
        Assert.Equal(10m, repository.SavedSnapshots.Single(item => !item.IsCashIn).LinkedSum);
        Assert.Contains("source-1", repository.SavedSnapshots.Single(item => !item.IsCashIn).OperationsBeforeJson);

        var paymentPayload = JsonNode.Parse(gateway.Batches[0].PayloadJson)!.AsObject();
        var paymentOperations = paymentPayload["operations"]!.AsArray();
        Assert.Equal(["keep-1", "keep-2"], paymentOperations.Select(item => item!["marker"]!.GetValue<string>()));
        Assert.DoesNotContain(sourceId.ToString("D"), gateway.Batches[0].PayloadJson);

        var cashPayload = JsonNode.Parse(gateway.Batches[1].PayloadJson)!.AsObject();
        Assert.Single(cashPayload["operations"]!.AsArray());
        Assert.Equal("keep-cash", cashPayload["operations"]![0]! ["marker"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReattachAsync_ReplacesPaymentAndCashLinksPreservingOperationOrderAndFields()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var paymentOperations = JsonSerializer.Serialize(new object[]
        {
            Operation("keep-payment", "paymentin", Guid.NewGuid()),
            Operation("source-payment", "purchasereturn", sourceId, 123.45m),
            Operation("keep-payment-2", "paymentin", Guid.NewGuid())
        });
        var cashOperations = JsonSerializer.Serialize(new object[]
        {
            Operation("source-cash", "purchasereturn", sourceId, 70000m),
            Operation("keep-cash", "cashin", Guid.NewGuid())
        });
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}", paymentOperations, 123.45m)],
            [new PurchaseReturnMoneyRelationSnapshot(cashId, "{}", cashOperations, 70000m)]));
        var gateway = new RecordingGateway();

        var result = await Service(repository, gateway).ReattachAsync(
            Guid.NewGuid(),
            [sourceId],
            new Dictionary<Guid, Guid> { [sourceId] = newId },
            CancellationToken.None);

        Assert.True(Assert.Single(result.Documents).IsSuccessful);
        Assert.Equal(["paymentin", "cashin"], gateway.Batches.Select(item => item.Type));
        var paymentPayload = JsonNode.Parse(gateway.Batches[0].PayloadJson)!.AsObject();
        var paymentItems = paymentPayload["operations"]!.AsArray();
        Assert.Equal(["keep-payment", "source-payment", "keep-payment-2"],
            paymentItems.Select(item => item!["marker"]!.GetValue<string>()));
        Assert.Contains(newId.ToString("D"), paymentItems[1]!.ToJsonString());
        Assert.DoesNotContain(sourceId.ToString("D"), paymentItems[1]!.ToJsonString());
        Assert.Equal(123.45m, paymentItems[1]!["linkedSum"]!.GetValue<decimal>());

        var cashPayload = JsonNode.Parse(gateway.Batches[1].PayloadJson)!.AsObject();
        Assert.Equal(["source-cash", "keep-cash"],
            cashPayload["operations"]!.AsArray()
                .Select(item => item!["marker"]!.GetValue<string>()));
        Assert.Contains(newId.ToString("D"), cashPayload["operations"]![0]!.ToJsonString());
    }

    [Fact]
    public async Task ReattachAsync_UsesOnePayloadForSharedMoneyDocument()
    {
        var firstSourceId = Guid.NewGuid();
        var secondSourceId = Guid.NewGuid();
        var firstNewId = Guid.NewGuid();
        var secondNewId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var operations = JsonSerializer.Serialize(new object[]
        {
            Operation("first", "purchasereturn", firstSourceId, 10m),
            Operation("second", "purchasereturn", secondSourceId, 20m),
            Operation("keep", "paymentin", Guid.NewGuid())
        });
        var repository = new RecordingRepository(
            new PurchaseReturnFactureRelationsSnapshot(
                firstSourceId,
                [],
                [],
                [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}", operations, 10m)],
                []),
            new PurchaseReturnFactureRelationsSnapshot(
                secondSourceId,
                [],
                [],
                [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}", operations, 20m)],
                []));
        var gateway = new RecordingGateway();

        var result = await Service(repository, gateway).ReattachAsync(
            Guid.NewGuid(),
            [firstSourceId, secondSourceId],
            new Dictionary<Guid, Guid>
            {
                [firstSourceId] = firstNewId,
                [secondSourceId] = secondNewId
            },
            CancellationToken.None);

        Assert.All(result.Documents, item => Assert.True(item.IsSuccessful));
        Assert.Single(gateway.Batches);
        Assert.Equal(3, JsonNode.Parse(gateway.Batches[0].PayloadJson)!["operations"]!.AsArray().Count);
        Assert.Contains(firstNewId.ToString("D"), gateway.Batches[0].PayloadJson);
        Assert.Contains(secondNewId.ToString("D"), gateway.Batches[0].PayloadJson);
        Assert.Contains("keep", gateway.Batches[0].PayloadJson);
    }

    [Fact]
    public async Task ReattachAsync_ReturnsSpecificFailureWhenBatchItemIsRejected()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var operations = JsonSerializer.Serialize(new object[]
        {
            Operation("source", "purchasereturn", sourceId, 10m)
        });
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}", operations, 10m)],
            []));
        var gateway = new RecordingGateway { RejectReattach = true };

        var result = await Service(repository, gateway).ReattachAsync(
            Guid.NewGuid(),
            [sourceId],
            new Dictionary<Guid, Guid> { [sourceId] = newId },
            CancellationToken.None);

        var failure = Assert.Single(result.Failed);
        Assert.Equal("PURCHASERETURN_PAYMENT_RELATIONS_REATTACH_FAILED", failure.ErrorCode);
        Assert.Contains($"paymentin {paymentId:D}", failure.Error);
        Assert.Contains(sourceId.ToString("D"), failure.Error);
    }

    [Fact]
    public async Task ReattachAsync_FailsWhenResponseDoesNotContainNewPurchaseReturnLink()
    {
        var sourceId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var operations = JsonSerializer.Serialize(new object[]
        {
            Operation("source", "purchasereturn", sourceId, 10m)
        });
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}", operations, 10m)],
            []));
        var gateway = new RecordingGateway { RemovePurchaseReturnFromResponse = true };

        var result = await Service(repository, gateway).ReattachAsync(
            Guid.NewGuid(),
            [sourceId],
            new Dictionary<Guid, Guid> { [sourceId] = newId },
            CancellationToken.None);

        var failure = Assert.Single(result.Failed);
        Assert.Equal("PURCHASERETURN_PAYMENT_RELATIONS_REATTACH_FAILED", failure.ErrorCode);
        Assert.Contains($"paymentin {paymentId:D}", failure.Error);
        Assert.Contains("new purchasereturn link is missing", failure.Error);
    }

    [Fact]
    public async Task CheckAsync_SendsEmptyOperationsWhenOnlyPurchaseReturnLinksExist()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var gateway = new RecordingGateway();
        gateway.Documents[(MoneyType.PaymentIn, paymentId)] = MoneyDocument(
            "paymentin",
            paymentId,
            Operation("source", "purchasereturn", sourceId, 1m));
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{\"meta\":{}}")],
            []));

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        Assert.Equal("Detached", Assert.Single(result.Documents).Status);
        var payload = JsonNode.Parse(gateway.Batches.Single().PayloadJson)!.AsObject();
        Assert.Empty(payload["operations"]!.AsArray());
    }

    [Fact]
    public async Task CheckAsync_SkipsFactureRelationsBeforeLoadingMoneyDocuments()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingGateway();
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            ["facture-in"],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(Guid.NewGuid(), "{}")],
            []));

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        Assert.Equal("PURCHASERETURN_FACTURE_RELATIONS_PRESENT", Assert.Single(result.Documents).ErrorCode);
        Assert.Empty(gateway.GetCalls);
        Assert.Empty(gateway.Batches);
    }

    [Fact]
    public async Task CheckAsync_SkipsWhenDetachResponseStillContainsOldPurchaseReturn()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var gateway = new RecordingGateway { KeepPurchaseReturnOnDetach = true, VerificationSourceId = sourceId };
        gateway.Documents[(MoneyType.PaymentIn, paymentId)] = MoneyDocument(
            "paymentin",
            paymentId,
            Operation("source", "purchasereturn", sourceId, 100m));
        var repository = new RecordingRepository(new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}")],
            []));

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        Assert.Equal(
            "PURCHASERETURN_PAYMENT_RELATIONS_DETACH_VERIFY_FAILED",
            Assert.Single(result.Documents).ErrorCode);
    }

    [Fact]
    public async Task CheckAsync_RollsBackOnlySourceRelationsAfterPartialDetachFailure()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var gateway = new RecordingGateway { RejectCashInDetach = true };
        gateway.Documents[(MoneyType.PaymentIn, paymentId)] = MoneyDocument(
            "paymentin",
            paymentId,
            Operation("source-payment", "purchasereturn", sourceId, 100m),
            Operation("keep", "paymentin", Guid.NewGuid()));
        gateway.Documents[(MoneyType.CashIn, cashId)] = MoneyDocument(
            "cashin",
            cashId,
            Operation("source-cash", "purchasereturn", sourceId, 200m));
        var snapshot = new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}")],
            [new PurchaseReturnMoneyRelationSnapshot(cashId, "{}")]);
        var repository = new RecordingRepository(snapshot);

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("Skipped", document.Status);
        Assert.Equal("PURCHASERETURN_PAYMENT_RELATIONS_DETACH_FAILED", document.ErrorCode);
        Assert.Equal(["paymentin", "cashin", "paymentin"], gateway.Batches.Select(item => item.Type));
        var rollback = JsonNode.Parse(gateway.Batches[2].PayloadJson)!.AsObject();
        Assert.Contains(sourceId.ToString("D"), rollback.ToJsonString());
    }

    [Fact]
    public async Task CheckAsync_UsesRollbackFailedWhenRestoreIsRejected()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var gateway = new RecordingGateway { RejectCashInDetach = true, RejectRollback = true };
        gateway.Documents[(MoneyType.PaymentIn, paymentId)] = MoneyDocument(
            "paymentin",
            paymentId,
            Operation("source", "purchasereturn", sourceId, 100m));
        var cashId = Guid.NewGuid();
        gateway.Documents[(MoneyType.CashIn, cashId)] = MoneyDocument(
            "cashin",
            cashId,
            Operation("cash", "purchasereturn", sourceId, 200m));
        var snapshot = new PurchaseReturnFactureRelationsSnapshot(
            sourceId,
            [],
            [],
            [new PurchaseReturnMoneyRelationSnapshot(paymentId, "{}")],
            [new PurchaseReturnMoneyRelationSnapshot(cashId, "{}")]);
        var repository = new RecordingRepository(snapshot);

        var result = await Service(repository, gateway).CheckAsync(
            Guid.NewGuid(),
            [sourceId],
            CancellationToken.None);

        Assert.Equal(
            "PURCHASERETURN_PAYMENT_RELATIONS_ROLLBACK_FAILED",
            Assert.Single(result.Documents).ErrorCode);
    }

    [Fact]
    public async Task Repository_SavesOperationsAndLinkedSumInMoneyRelationTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.PurchaseReturnRawData.Add(new PurchaseReturnRawData
        {
            AccountId = accountId,
            DocumentId = sourceId,
            RawJson = "{}"
        });
        dbContext.PurchaseReturnPaymentInRawData.Add(new PurchaseReturnPaymentInRawData
        {
            AccountId = accountId,
            PurchaseReturnId = sourceId,
            DocumentId = paymentId,
            RawJson = "{}"
        });
        await dbContext.SaveChangesAsync();

        var repository = new PurchaseReturnFactureRelationsRepository(dbContext);
        await repository.SaveMoneyRelationSnapshotsAsync(
            accountId,
            [new PurchaseReturnMoneyRelationSnapshotUpdate(
                sourceId,
                paymentId,
                false,
                "[{\"marker\":\"first\"},{\"marker\":\"second\"}]",
                123.45m)],
            CancellationToken.None);

        var saved = await dbContext.PurchaseReturnPaymentInRawData.SingleAsync();
        Assert.Equal("[{\"marker\":\"first\"},{\"marker\":\"second\"}]", saved.OperationsBeforeJson);
        Assert.Equal(123.45m, saved.LinkedSum);
    }

    [Fact]
    public async Task Gateway_UsesExpandedGetAndSeparateBatchEndpoints()
    {
        var paymentId = Guid.NewGuid();
        var cashId = Guid.NewGuid();
        var requests = new List<string>();
        var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Post)
                return Response(HttpStatusCode.OK, request.Content!.ReadAsStringAsync().Result);
            return Response(HttpStatusCode.OK, "{\"operations\":[]}");
        }))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        var gateway = new MoySkladPurchaseReturnMoneyRelationsGateway(
            client,
            new FakeTokenClient(),
            new NoopRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladPurchaseReturnMoneyRelationsGateway>.Instance);

        await gateway.GetPaymentInAsync(Guid.NewGuid(), paymentId, "correlation", CancellationToken.None);
        await gateway.GetCashInAsync(Guid.NewGuid(), cashId, "correlation", CancellationToken.None);
        await gateway.PaymentInBatchAsync(
            Guid.NewGuid(),
            "correlation",
            [new MoySkladPurchaseReturnMoneyRelationBatchItem(paymentId, MetaPayload("paymentin", paymentId))],
            CancellationToken.None);
        await gateway.CashInBatchAsync(
            Guid.NewGuid(),
            "correlation",
            [new MoySkladPurchaseReturnMoneyRelationBatchItem(cashId, MetaPayload("cashin", cashId))],
            CancellationToken.None);

        Assert.Equal(
            [
                $"/api/remap/1.2/entity/paymentin/{paymentId:D}?expand=operations",
                $"/api/remap/1.2/entity/cashin/{cashId:D}?expand=operations",
                "/api/remap/1.2/entity/paymentin/batch",
                "/api/remap/1.2/entity/cashin/batch"
            ],
            requests);
    }

    private static PurchaseReturnFactureRelationsService Service(
        RecordingRepository repository,
        RecordingGateway gateway) =>
        new(
            repository,
            NullLogger<PurchaseReturnFactureRelationsService>.Instance,
            gateway,
            repository);

    private static string MoneyDocument(string type, Guid id, params object[] operations) =>
        JsonSerializer.Serialize(new
        {
            id,
            meta = new
            {
                href = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
                type,
                mediaType = "application/json"
            },
            operations
        });

    private static object Operation(string marker, string type, Guid id, decimal? linkedSum = null) =>
        new
        {
            marker,
            linkedSum,
            meta = new
            {
                href = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
                type,
                mediaType = "application/json"
            }
        };

    private static string MetaPayload(string type, Guid id) =>
        JsonSerializer.Serialize(new
        {
            meta = new
            {
                href = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
                type,
                mediaType = "application/json"
            },
            operations = Array.Empty<object>()
        });

    private sealed class RecordingRepository(
        params PurchaseReturnFactureRelationsSnapshot[] snapshots)
        : IPurchaseReturnFactureRelationsRepository, IPurchaseReturnMoneyRelationsRepository
    {
        private readonly IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot> _snapshots =
            snapshots.ToDictionary(item => item.PurchaseReturnId);

        public List<PurchaseReturnMoneyRelationSnapshotUpdate> SavedSnapshots { get; } = [];

        public Task<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>> GetSnapshotsAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> purchaseReturnIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, PurchaseReturnFactureRelationsSnapshot>>(
                purchaseReturnIds
                    .Where(id => _snapshots.ContainsKey(id))
                    .ToDictionary(id => id, id => _snapshots[id]));

        public Task SaveMoneyRelationSnapshotsAsync(
            Guid accountId,
            IReadOnlyCollection<PurchaseReturnMoneyRelationSnapshotUpdate> snapshots,
            CancellationToken cancellationToken)
        {
            SavedSnapshots.AddRange(snapshots);
            return Task.CompletedTask;
        }
    }

    private enum MoneyType
    {
        PaymentIn,
        CashIn
    }

    private sealed class RecordingGateway : IMoySkladPurchaseReturnMoneyRelationsGateway
    {
        public Dictionary<(MoneyType Type, Guid Id), string> Documents { get; } = [];

        public List<string> GetCalls { get; } = [];

        public List<(string Type, string PayloadJson)> Batches { get; } = [];

        public bool RejectCashInDetach { get; init; }

        public bool RejectRollback { get; init; }

        public bool RejectReattach { get; init; }

        public bool RemovePurchaseReturnFromResponse { get; init; }

        public bool KeepPurchaseReturnOnDetach { get; init; }

        public Guid? VerificationSourceId { get; init; }

        public Task<string> GetPaymentInAsync(Guid accountId, Guid documentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(Get(MoneyType.PaymentIn, documentId));

        public Task<string> GetCashInAsync(Guid accountId, Guid documentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(Get(MoneyType.CashIn, documentId));

        public Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> PaymentInBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(Batch("paymentin", items));

        public Task<IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult>> CashInBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items,
            CancellationToken cancellationToken) =>
            Task.FromResult(Batch("cashin", items));

        private IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchResult> Batch(
            string type,
            IReadOnlyList<MoySkladPurchaseReturnMoneyRelationBatchItem> items)
        {
            var isRollback = items.Any(item => item.PayloadJson.Contains("purchasereturn", StringComparison.OrdinalIgnoreCase));
            Batches.AddRange(items.Select(item => (type, item.PayloadJson)));
            return items.Select(item =>
            {
                if (RejectReattach ||
                    (RejectCashInDetach && type == "cashin" && !isRollback) ||
                    (RejectRollback && isRollback))
                {
                    return new MoySkladPurchaseReturnMoneyRelationBatchResult(
                        item.DocumentId,
                        "{}",
                        "REJECTED",
                        "rejected");
                }

                var responseJson = item.PayloadJson;
                if (RemovePurchaseReturnFromResponse)
                {
                    var payload = JsonNode.Parse(responseJson)!.AsObject();
                    payload["operations"]!.AsArray().Clear();
                    responseJson = payload.ToJsonString();
                }
                if (KeepPurchaseReturnOnDetach && !isRollback && VerificationSourceId is not null)
                {
                    var payload = JsonNode.Parse(responseJson)!.AsObject();
                    payload["operations"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(
                        Operation("still-present", "purchasereturn", VerificationSourceId.Value, 1m))));
                    responseJson = payload.ToJsonString();
                }

                return new MoySkladPurchaseReturnMoneyRelationBatchResult(item.DocumentId, responseJson);
            }).ToArray();
        }

        private string Get(MoneyType type, Guid documentId)
        {
            GetCalls.Add($"{type}:{documentId:D}");
            return Documents[(type, documentId)];
        }
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("test-token");
    }

    private sealed class NoopRateLimiter : IMoySkladRateLimiter
    {
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ObserveAsync(Guid accountId, MoySkladRateLimitObservation observation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
