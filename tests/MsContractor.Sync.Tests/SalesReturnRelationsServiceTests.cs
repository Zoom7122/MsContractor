using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.Sync.Tests;

public sealed class SalesReturnRelationsServiceTests
{
    [Fact]
    public async Task PrepareAndReattachAsync_PreservesOperationsAndSeparatesDocumentTypes()
    {
        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var cashOutId = Guid.NewGuid();
        var lossId = Guid.NewGuid();
        var newSalesReturnId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, paymentId, cashOutId, lossId);
        var repository = new RecordingRelationsRepository();
        var service = new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance);
        var operation = Operation(accountId, operationId, sourceId);
        await service.PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.True(item.Stage == "Prepared", item.Error);
        Assert.Equal("RelationsDetached", item.RelationsStatus);
        Assert.Equal(["paymentout", "cashout", "loss"], gateway.Batches.Take(3).Select(item => item.Type));

        var paymentDetach = JsonNode.Parse(gateway.Batches[0].Payloads.Single())!.AsObject();
        var paymentOperations = paymentDetach["operations"]!.AsArray();
        Assert.Single(paymentOperations);
        Assert.Equal("keep", paymentOperations[0]! ["marker"]!.GetValue<string>());
        Assert.DoesNotContain(sourceId.ToString("D"), paymentDetach.ToJsonString());

        var cashDetach = JsonNode.Parse(gateway.Batches[1].Payloads.Single())!.AsObject();
        Assert.Single(cashDetach["operations"]!.AsArray());
        Assert.Equal("keep", cashDetach["operations"]![0]! ["marker"]!.GetValue<string>());

        var lossDetach = JsonNode.Parse(gateway.Batches[2].Payloads.Single())!.AsObject();
        Assert.True(lossDetach.ContainsKey("salesReturn"));
        Assert.Null(lossDetach["salesReturn"]);

        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal(sourceId, snapshot.SourceSalesReturnId);
        Assert.Equal(100m, Assert.Single(snapshot.PaymentOuts).LinkedSum);
        Assert.Equal(200m, Assert.Single(snapshot.CashOuts).LinkedSum);
        Assert.Equal(lossId, Assert.Single(snapshot.Losses).DocumentId);

        item.NewDocumentId = newSalesReturnId;
        item.Stage = "Created";
        await service.ReattachAsync(operation, "correlation-id", CancellationToken.None);

        Assert.True(item.Stage == "Completed", item.Error);
        Assert.Equal("RelationsReattached", item.RelationsStatus);
        Assert.Equal(["paymentout", "cashout", "loss", "paymentout", "cashout", "loss"],
            gateway.Batches.Select(item => item.Type));

        var paymentReattach = JsonNode.Parse(gateway.Batches[3].Payloads.Single())!.AsObject();
        var reattachedOperation = paymentReattach["operations"]!.AsArray().Single(node =>
            node!["meta"]!["href"]!.GetValue<string>().EndsWith(newSalesReturnId.ToString("D")));
        Assert.Equal(100m, reattachedOperation!["linkedSum"]!.GetValue<decimal>());
        Assert.Equal("keep", paymentReattach["operations"]![1]! ["marker"]!.GetValue<string>());

        var lossReattach = JsonNode.Parse(gateway.Batches[5].Payloads.Single())!.AsObject();
        Assert.EndsWith(newSalesReturnId.ToString("D"),
            lossReattach["salesReturn"]!["meta"]!["href"]!.GetValue<string>());
    }

    [Fact]
    public async Task PrepareAndDetachAsync_MissingLinkedSumSkipsSourceAndDoesNotWriteBatch()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, paymentId, Guid.NewGuid(), Guid.NewGuid())
        {
            MissingLinkedSum = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);

        await new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance)
            .PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.Equal("Skipped", item.Stage);
        Assert.Equal("SALESRETURN_RELATIONS_LOAD_FAILED", item.ErrorCode);
        Assert.Equal("Skipped", item.RelationsStatus);
        Assert.Empty(gateway.Batches);
        Assert.Equal("Skipped", Assert.Single(repository.SourceStatuses).Status);
        Assert.Empty(Assert.Single(repository.Snapshots).PaymentOuts);
    }

    [Fact]
    public async Task PrepareAndDetachAsync_ZeroLinkedSumIsValid()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            ZeroLinkedSum = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);

        await new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance)
            .PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.Equal("Prepared", item.Stage);
        Assert.Equal("RelationsDetached", item.RelationsStatus);
        Assert.Equal(3, gateway.Batches.Count);
        Assert.Equal(0m, Assert.Single(Assert.Single(repository.Snapshots).PaymentOuts).LinkedSum);
    }

    [Fact]
    public async Task PrepareAndReattachAsync_MultipleSourceOperationsAreMovedAndOtherOperationsAreKept()
    {
        var sourceId = Guid.NewGuid();
        var newSalesReturnId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            MultipleSourceOperations = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);
        var service = new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance);

        await service.PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        Assert.Equal("RelationsDetached", Assert.Single(operation.Items).RelationsStatus);
        var paymentDetach = JsonNode.Parse(gateway.Batches[0].Payloads.Single())!["operations"]!.AsArray();
        Assert.Single(paymentDetach);
        Assert.Equal("keep", paymentDetach[0]!["marker"]!.GetValue<string>());
        var cashDetach = JsonNode.Parse(gateway.Batches[1].Payloads.Single())!["operations"]!.AsArray();
        Assert.Single(cashDetach);
        Assert.Equal("keep", cashDetach[0]!["marker"]!.GetValue<string>());

        var item = Assert.Single(operation.Items);
        item.NewDocumentId = newSalesReturnId;
        item.Stage = "Created";
        await service.ReattachAsync(operation, "correlation-id", CancellationToken.None);

        Assert.Equal("Completed", item.Stage);
        var paymentReattach = JsonNode.Parse(gateway.Batches[3].Payloads.Single())!["operations"]!.AsArray();
        Assert.Equal(3, paymentReattach.Count);
        Assert.Equal(2, paymentReattach.Count(node =>
            node!["meta"]!["href"]!.GetValue<string>().EndsWith(newSalesReturnId.ToString("D"))));
        Assert.Equal("keep", paymentReattach[2]!["marker"]!.GetValue<string>());
    }

    [Fact]
    public async Task PrepareAndDetachAsync_EmptyRelationArraysAreSuccessful()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            EmptyRelations = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);

        await new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance)
            .PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.Equal("Prepared", item.Stage);
        Assert.Equal("RelationsDetached", item.RelationsStatus);
        Assert.Empty(gateway.Batches);
        var snapshot = Assert.Single(repository.Snapshots);
        Assert.Equal(sourceId, snapshot.SourceSalesReturnId);
        Assert.Empty(snapshot.PaymentOuts);
        Assert.Empty(snapshot.CashOuts);
        Assert.Empty(snapshot.Losses);
    }

    [Fact]
    public async Task PrepareAndDetachAsync_AcceptsMoySkladOmittedEmptyRelationFields()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(sourceId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            OmitEmptyDetachedFields = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);

        await new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance)
            .PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.Equal("Prepared", item.Stage);
        Assert.Equal("RelationsDetached", item.RelationsStatus);
        Assert.Equal("RelationsDetached", Assert.Single(repository.SourceStatuses).Status);
    }

    [Fact]
    public async Task PrepareAndDetachAsync_RollsBackAlreadyDetachedRelationsWhenAnotherTypeFails()
    {
        var sourceId = Guid.NewGuid();
        var gateway = new RecordingRelationsGateway(
            sourceId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid())
        {
            RejectCashOutDetach = true
        };
        var repository = new RecordingRelationsRepository();
        var operation = Operation(Guid.NewGuid(), Guid.NewGuid(), sourceId);

        await new SalesReturnRelationsService(gateway, repository, NullLogger<SalesReturnRelationsService>.Instance)
            .PrepareAndDetachAsync(operation, "correlation-id", CancellationToken.None);

        var item = Assert.Single(operation.Items);
        Assert.Equal("Skipped", item.Stage);
        Assert.Equal("RelationsRestored", item.RelationsStatus);
        Assert.Equal("CASHOUT_REJECTED", item.ErrorCode);
        Assert.Equal(["paymentout", "cashout", "loss", "paymentout", "loss"],
            gateway.Batches.Select(item => item.Type));
        Assert.Contains("salesreturn", gateway.Batches[3].Payloads.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gateway_UsesExpandedEndpointsAndCommonRateLimiter()
    {
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var requests = new List<string>();
        var limiter = new CountingRateLimiter();
        var client = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            if (request.Method == HttpMethod.Post)
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new[] { new { id = paymentId, operations = Array.Empty<object>() } }));
            return Response(HttpStatusCode.OK, "{\"id\":\"" + sourceId + "\"}");
        }))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        var gateway = new MoySkladSalesReturnRelationsGateway(
            client,
            new FakeTokenClient(),
            limiter,
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladSalesReturnRelationsGateway>.Instance);

        await gateway.GetSalesReturnRelationsAsync(Guid.NewGuid(), sourceId, "correlation-id", CancellationToken.None);
        await gateway.GetPaymentOutAsync(Guid.NewGuid(), paymentId, "correlation-id", CancellationToken.None);
        await gateway.PaymentOutBatchAsync(
            Guid.NewGuid(),
            "correlation-id",
            [new MoySkladRelationBatchItem(paymentId, JsonSerializer.Serialize(new { meta = Meta("paymentout", paymentId), operations = Array.Empty<object>() }))],
            CancellationToken.None);

        Assert.Equal(
            [
                $"/api/remap/1.2/entity/salesreturn/{sourceId:D}?expand=payments,losses",
                $"/api/remap/1.2/entity/paymentout/{paymentId:D}?expand=operations",
                "/api/remap/1.2/entity/paymentout/batch"
            ],
            requests);
        Assert.Equal(3, limiter.WaitCount);
        Assert.Equal(3, limiter.ObserveCount);
    }

    [Fact]
    public async Task RelationsRepository_PersistsSourceAndTypedSnapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        dbContext.SalesReturnRawData.Add(new SalesReturnRawData
        {
            AccountId = accountId,
            DocumentId = sourceId,
            RawJson = "{}"
        });
        dbContext.SalesReturnRecreationOperations.Add(Operation(accountId, operationId, sourceId));
        await dbContext.SaveChangesAsync();

        var repository = new SalesReturnRelationsRepository(dbContext);
        using var operationsDocument = JsonDocument.Parse("[{\"salesReturn\":{\"meta\":{\"type\":\"salesreturn\"}},\"linkedSum\":100}]");
        await repository.CreateSnapshotsAsync(
            accountId,
            operationId,
            [new SalesReturnRelationsSnapshot
            {
                SourceSalesReturnId = sourceId,
                PaymentOuts =
                [new PaymentOutRelationSnapshot
                {
                    DocumentId = paymentId,
                    OperationsBefore = operationsDocument.RootElement.EnumerateArray().Select(item => item.Clone()).ToArray(),
                    LinkedSum = 100
                }]
            }],
            CancellationToken.None);

        var saved = await dbContext.SalesReturnRelationsSnapshots
            .Include(item => item.PaymentOuts)
            .SingleAsync();
        Assert.Equal(sourceId, saved.SourceSalesReturnId);
        Assert.Equal("Prepared", saved.Status);
        Assert.Equal(paymentId, Assert.Single(saved.PaymentOuts).DocumentId);
        Assert.Equal(100m, Assert.Single(saved.PaymentOuts).LinkedSum);
        Assert.Contains("linkedSum", saved.PaymentOuts[0].OperationsBeforeJson);
    }

    private static SalesReturnRecreationOperation Operation(Guid accountId, Guid operationId, Guid sourceId)
    {
        var operation = new SalesReturnRecreationOperation
        {
            AccountId = accountId,
            OperationId = operationId,
            MainAgentId = Guid.NewGuid(),
            Status = "Prepared"
        };
        operation.Items.Add(new SalesReturnRecreationItem
        {
            AccountId = accountId,
            OperationId = operationId,
            SourceDocumentId = sourceId,
            NewSyncId = Guid.NewGuid(),
            Stage = "Prepared"
        });
        return operation;
    }

    private static object Meta(string type, Guid id) => new
    {
        meta = EntityMeta(type, id)
    };

    private static object EntityMeta(string type, Guid id) => new
    {
        href = $"https://api.moysklad.ru/api/remap/1.2/entity/{type}/{id:D}",
        type,
        mediaType = "application/json"
    };

    private static string ResponseSource(Guid sourceId, Guid paymentId, Guid cashOutId, Guid lossId) =>
        JsonSerializer.Serialize(new
        {
            id = sourceId,
            meta = EntityMeta("salesreturn", sourceId),
            payments = new[] { Meta("paymentout", paymentId), Meta("cashout", cashOutId) },
            losses = new[] { Meta("loss", lossId) }
        });

    private sealed class RecordingRelationsGateway : IMoySkladSalesReturnRelationsGateway
    {
        private readonly Guid _sourceId;
        private readonly Guid? _paymentId;
        private readonly Guid? _cashOutId;
        private readonly Guid? _lossId;

        public RecordingRelationsGateway(Guid sourceId, Guid? paymentId, Guid? cashOutId, Guid? lossId)
        {
            _sourceId = sourceId;
            _paymentId = paymentId;
            _cashOutId = cashOutId;
            _lossId = lossId;
        }

        public bool MissingLinkedSum { get; set; }

        public bool ZeroLinkedSum { get; set; }

        public bool RejectCashOutDetach { get; set; }

        public bool EmptyRelations { get; set; }

        public bool MultipleSourceOperations { get; set; }

        public bool OmitEmptyDetachedFields { get; set; }

        public List<(string Type, IReadOnlyList<string> Payloads)> Batches { get; } = [];

        public Task<string> GetSalesReturnRelationsAsync(Guid accountId, Guid salesReturnId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(EmptyRelations
                ? JsonSerializer.Serialize(new
                {
                    id = _sourceId,
                    meta = EntityMeta("salesreturn", _sourceId),
                    payments = Array.Empty<object>(),
                    losses = Array.Empty<object>()
                })
                : ResponseSource(_sourceId, _paymentId!.Value, _cashOutId!.Value, _lossId!.Value));

        public Task<string> GetPaymentOutAsync(Guid accountId, Guid documentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(MoneyDocument("paymentout", documentId, _sourceId, ZeroLinkedSum ? 0m : 100m, includeOther: true,
                missingLinkedSum: MissingLinkedSum, multipleSourceOperations: MultipleSourceOperations));

        public Task<string> GetCashOutAsync(Guid accountId, Guid documentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(MoneyDocument("cashout", documentId, _sourceId, 200m, includeOther: true,
                missingLinkedSum: false, multipleSourceOperations: MultipleSourceOperations));

        public Task<string> GetLossAsync(Guid accountId, Guid documentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult(JsonSerializer.Serialize(new
            {
                id = documentId,
                meta = EntityMeta("loss", documentId),
                salesReturn = Meta("salesreturn", _sourceId)
            }));

        public Task<IReadOnlyList<MoySkladRelationBatchResult>> PaymentOutBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladRelationBatchItem> items, CancellationToken cancellationToken) =>
            Task.FromResult(Batch("paymentout", items));

        public Task<IReadOnlyList<MoySkladRelationBatchResult>> CashOutBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladRelationBatchItem> items, CancellationToken cancellationToken) =>
            Task.FromResult(Batch("cashout", items));

        public Task<IReadOnlyList<MoySkladRelationBatchResult>> LossBatchAsync(Guid accountId, string correlationId, IReadOnlyList<MoySkladRelationBatchItem> items, CancellationToken cancellationToken) =>
            Task.FromResult(Batch("loss", items));

        private IReadOnlyList<MoySkladRelationBatchResult> Batch(string type, IReadOnlyList<MoySkladRelationBatchItem> items)
        {
            Batches.Add((type, items.Select(item => item.PayloadJson).ToArray()));
            return items.Select(item =>
            {
                if (RejectCashOutDetach && type == "cashout")
                    return new MoySkladRelationBatchResult(item.DocumentId, item.PayloadJson, "CASHOUT_REJECTED", "cashout rejected");

                var responseJson = item.PayloadJson;
                if (OmitEmptyDetachedFields)
                {
                    var response = JsonNode.Parse(responseJson)?.AsObject()
                        ?? throw new InvalidOperationException("The fake relation payload is not an object.");
                    if (type == "loss" && response["salesReturn"] is null)
                        response.Remove("salesReturn");
                    if (type != "loss" && response["operations"] is JsonArray operations && operations.Count == 0)
                        response.Remove("operations");
                    responseJson = response.ToJsonString();
                }

                return new MoySkladRelationBatchResult(item.DocumentId, responseJson);
            }).ToArray();
        }

        private static string MoneyDocument(
            string type,
            Guid documentId,
            Guid sourceId,
            decimal linkedSum,
            bool includeOther,
            bool missingLinkedSum,
            bool multipleSourceOperations)
        {
            var operations = new List<object>
            {
                missingLinkedSum
                    ? new { meta = EntityMeta("salesreturn", sourceId) }
                    : new { meta = EntityMeta("salesreturn", sourceId), linkedSum, marker = "linked" }
            };
            if (multipleSourceOperations)
                operations.Add(new { meta = EntityMeta("salesreturn", sourceId), linkedSum = 25m, marker = "linked-second" });
            if (includeOther)
                operations.Add(new { meta = EntityMeta("salesreturn", Guid.NewGuid()), linkedSum = 50m, marker = "keep" });
            return JsonSerializer.Serialize(new
            {
                id = documentId,
                meta = EntityMeta(type, documentId),
                operations
            });
        }
    }

    private sealed class RecordingRelationsRepository : ISalesReturnRelationsRepository
    {
        public List<SalesReturnRelationsSnapshot> Snapshots { get; } = [];

        public List<(Guid SourceId, string Status, string? ErrorCode, string? Error)> SourceStatuses { get; } = [];

        public Task CreateSnapshotsAsync(Guid accountId, Guid operationId, IReadOnlyCollection<SalesReturnRelationsSnapshot> snapshots, CancellationToken cancellationToken)
        {
            Snapshots.AddRange(snapshots);
            return Task.CompletedTask;
        }

        public Task UpdateSourceStatusAsync(Guid accountId, Guid operationId, Guid sourceSalesReturnId, string status, string? errorCode, string? error, CancellationToken cancellationToken)
        {
            SourceStatuses.Add((sourceSalesReturnId, status, errorCode, error));
            return Task.CompletedTask;
        }

        public Task UpdateDocumentStatusAsync(Guid accountId, Guid operationId, Guid sourceSalesReturnId, SalesReturnRelationDocumentType documentType, Guid documentId, string detachStatus, string reattachStatus, string? errorCode, string? error, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("secret-token");
    }

    private sealed class CountingRateLimiter : IMoySkladRateLimiter
    {
        public int WaitCount { get; private set; }

        public int ObserveCount { get; private set; }

        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken)
        {
            WaitCount++;
            return Task.CompletedTask;
        }

        public Task ObserveAsync(Guid accountId, MoySkladRateLimitObservation observation, CancellationToken cancellationToken)
        {
            ObserveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) => _callback = callback;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_callback(request));
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}
