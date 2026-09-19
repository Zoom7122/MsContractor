using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents.Salesreturn;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.ResponseHandling;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.Sync.Tests;

public sealed class MoySkladSalesReturnTests
{
    [Fact]
    public async Task Gateway_GetAsync_SendsUpToOneThousandIdsAsOneRequestWithoutPositions()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        var requests = new List<HttpRequestMessage>();
        var gateway = CreateGateway(request =>
        {
            requests.Add(request);
            return Response(HttpStatusCode.OK, Collection(ids));
        });

        var result = await gateway.GetAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", ids, CancellationToken.None);

        var request = Assert.Single(requests);
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.Contains("filter=id=", query);
        Assert.Contains("limit=1000", query);
        Assert.DoesNotContain("expand=positions", query);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", request.Headers.Authorization?.Parameter);
        Assert.Equal(ids.ToHashSet(), result.Keys.ToHashSet());
        Assert.DoesNotContain("\"positions\"", result[ids[0]]);
    }

    [Fact]
    public async Task Gateway_GetAsync_RejectsMissingDocumentFromResponse()
    {
        var requested = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var gateway = CreateGateway(_ => Response(
            HttpStatusCode.OK,
            Collection([requested[0]])));

        await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", requested, CancellationToken.None));
    }

    [Fact]
    public async Task Gateway_GetAsync_MapsMoySkladHttpErrorToEgressException()
    {
        var gateway = CreateGateway(_ => Response(
            HttpStatusCode.TooManyRequests,
            "{\"errors\":[{\"error\":\"rate limited\"}]}"));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id",
            [Guid.NewGuid()], CancellationToken.None));

        Assert.Equal("MOYSKLAD_RATE_LIMITED", exception.Code);
        Assert.True(exception.Retryable);
    }

    [Fact]
    public async Task Gateway_GetAsync_RejectsDuplicateRequestedIds()
    {
        var id = Guid.NewGuid();
        var gateway = CreateGateway(_ => Response(HttpStatusCode.OK, Collection([id])));

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [id, id], CancellationToken.None));
    }

    [Fact]
    public async Task Gateway_DeleteBatchAsync_UsesBulkDeleteEndpoint()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        HttpRequestMessage? request = null;
        var gateway = CreateGateway(message =>
        {
            request = message;
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(ids.Select(id => new
            {
                meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/salesreturn/{id:D}" }
            })));
        });

        var result = await gateway.DeleteBatchAsync(
            Guid.NewGuid(), "correlation-id", ids, CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/remap/1.2/entity/salesreturn/delete", request.RequestUri!.AbsolutePath);
        Assert.All(result, item => Assert.True(item.Succeeded));
    }

    [Fact]
    public async Task Gateway_CreateBatchAsync_UsesBatchEndpointAndCorrelatesSyncId()
    {
        var sourceId = Guid.NewGuid();
        var syncId = Guid.NewGuid();
        var createdId = Guid.NewGuid();
        HttpRequestMessage? request = null;
        var gateway = CreateGateway(message =>
        {
            request = message;
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new[]
            {
                new { id = createdId, syncId, agent = new { meta = new { href = "https://api.moysklad.ru/api/remap/1.2/entity/counterparty/" + Guid.NewGuid(), type = "counterparty" } } }
            }));
        });

        var result = await gateway.CreateBatchAsync(
            Guid.NewGuid(),
            "correlation-id",
            [new MoySkladSalesReturnBatchCreateItem(sourceId, syncId, "{\"syncId\":\"" + syncId + "\"}")],
            CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/remap/1.2/entity/salesreturn/batch", request.RequestUri!.AbsolutePath);
        Assert.Equal(createdId, Assert.Single(result).DocumentId);
    }

    [Fact]
    public async Task Service_LoadAsync_SplitsIdsIntoThousandItemBatches()
    {
        var ids = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToArray();
        var gateway = new RecordingGateway();
        var repository = new RecordingRepository();
        var service = new MoySkladSalesReturnServiceGetData(gateway, repository);

        await service.LoadAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id",
            ids,
            CancellationToken.None);

        Assert.Collection(
            gateway.Requests,
            first => Assert.True(first.Count == 1000),
            second => Assert.True(second.Count == 1));
        Assert.Collection(
            repository.Batches,
            first => Assert.True(first.Count == 1000),
            second => Assert.True(second.Count == 1));
    }

    [Fact]
    public async Task Repository_UpsertAsync_OverwritesExistingRawJson()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EgressDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var dbContext = new EgressDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var repository = new SalesReturnRawDataRepository(dbContext);
        var documentId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();

        await repository.UpsertAsync(
            accountId,
            new Dictionary<Guid, string> { [documentId] = "{\"version\":1}" },
            CancellationToken.None);
        await repository.UpsertAsync(
            accountId,
            new Dictionary<Guid, string> { [documentId] = "{\"version\":2}" },
            CancellationToken.None);
        await repository.UpsertAsync(
            otherAccountId,
            new Dictionary<Guid, string> { [documentId] = "{\"version\":\"other-account\"}" },
            CancellationToken.None);

        var saved = await dbContext.SalesReturnRawData.ToListAsync();
        var accountDocument = saved.Single(item => item.AccountId == accountId);
        var otherAccountDocument = saved.Single(item => item.AccountId == otherAccountId);
        Assert.Equal(documentId, accountDocument.DocumentId);
        Assert.Equal("{\"version\":2}", accountDocument.RawJson);
        Assert.Equal("{\"version\":\"other-account\"}", otherAccountDocument.RawJson);
    }

    [Fact]
    public async Task Service_LoadAsync_StopsBeforeGatewayWhenCancelled()
    {
        var gateway = new RecordingGateway();
        var repository = new RecordingRepository();
        var service = new MoySkladSalesReturnServiceGetData(gateway, repository);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.LoadAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id",
            new[] { Guid.NewGuid() },
            cancellation.Token));

        Assert.Empty(gateway.Requests);
        Assert.Empty(repository.Batches);
    }

    private static MoySkladSalesReturnGateway CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> callback)
    {
        var client = new HttpClient(new StubHandler(callback))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        return new MoySkladSalesReturnGateway(
            client,
            new FakeTokenClient(),
            new FakeRateLimiter(),
            new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance),
            NullLogger<MoySkladSalesReturnGateway>.Instance);
    }

    private static string Collection(IReadOnlyList<Guid> ids) => JsonSerializer.Serialize(new
    {
        meta = new { size = ids.Count, limit = 1000, offset = 0 },
        rows = ids.Select(id => new { id })
    });

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingGateway : IMoySkladSalesReturnGateway
    {
        public List<IReadOnlyList<Guid>> Requests { get; } = [];

        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            IReadOnlyList<Guid> salesReturnIds,
            CancellationToken cancellationToken)
        {
            Requests.Add(salesReturnIds);
            return Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                salesReturnIds.ToDictionary(id => id, _ => "{\"id\":\"document\"}"));
        }

        public Task<IReadOnlyList<MoySkladSalesReturnAgentAccount>> GetAgentAccountsAsync(
            Guid accountId, Guid mainAgentId, string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladSalesReturnAgentAccount>>([]);

        public Task<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>> DeleteBatchAsync(
            Guid accountId, string correlationId, IReadOnlyList<Guid> salesReturnIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladSalesReturnBatchDeleteResult>>(
                salesReturnIds.Select(id => new MoySkladSalesReturnBatchDeleteResult(id, true)).ToArray());

        public Task<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>> CreateBatchAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<MoySkladSalesReturnBatchCreateItem> documents,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MoySkladSalesReturnBatchCreateResult>>([]);
    }

    private sealed class RecordingRepository : ISalesReturnRawDataRepository
    {
        public List<IReadOnlyDictionary<Guid, string>> Batches { get; } = [];

        public Task UpsertAsync(
            Guid accountId,
            IReadOnlyDictionary<Guid, string> documents,
            CancellationToken cancellationToken)
        {
            Batches.Add(documents);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<Guid, string>> GetRequiredAsync(
            Guid accountId,
            IReadOnlyCollection<Guid> documentIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("secret-token");
    }

    private sealed class FakeRateLimiter : IMoySkladRateLimiter
    {
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ObserveAsync(
            Guid accountId,
            MoySkladRateLimitObservation observation,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _callback;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback)
        {
            _callback = callback;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(_callback(request));
    }
}
