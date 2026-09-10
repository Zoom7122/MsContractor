using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.Sync.Tests;

public sealed class SalesReturnGatewayTests
{
    [Fact]
    public void TypedGatewayHasAnUnambiguousConstructor()
    {
        Assert.NotNull(ActivatorUtilities.CreateFactory(typeof(MoySkladSalesReturnGateway), [typeof(HttpClient)]));
    }

    [Fact]
    public void MigrationIsDiscoveredAndSnapshotMatchesModel()
    {
        using var db = new EgressDbContextFactory().CreateDbContext([]);
        Assert.EndsWith("_AddSalesReturnOperationJournal", Assert.Single(db.Database.GetMigrations()));
        Assert.False(db.Database.HasPendingModelChanges());
        var snapshot = db.GetService<IMigrationsAssembly>().ModelSnapshot!;
        Assert.Contains(snapshot.Model.GetEntityTypes(), x => x.GetTableName() == "salesreturn_claims");
    }

    [Fact]
    public async Task CreateUsesBatchArrayAndMatchesReversedResultsBySyncId()
    {
        var request = SalesReturnRecreationTests.Request(2);
        var items = request.Documents.Select(x =>
        {
            var sync = Guid.NewGuid();
            return new SalesReturnOperationItem { OldDocumentId = x.OldDocumentId, SyncId = sync,
                Payload = SalesReturnRecreationTests.Builder().Build(x, request.MainCounterpartyId, sync) };
        }).ToArray();
        var limiter = new Limiter();
        var gateway = Gateway(async message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.EndsWith("entity/salesreturn/batch", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("test-token", message.Headers.Authorization.Parameter);
            var body = JsonNode.Parse(await message.Content!.ReadAsStringAsync())!.AsArray();
            Assert.Equal(2, body.Count);
            Assert.All(body, item => Assert.Null(item!["id"]));
            var result = new JsonArray(items.Reverse().Select(x => (JsonNode?)new JsonObject
                { ["id"] = Guid.NewGuid(), ["syncId"] = x.SyncId, ["agent"] = SalesReturnRecreationTests.Reference("counterparty", request.MainCounterpartyId) }).ToArray());
            return Response(result.ToJsonString());
        }, limiter);
        var context = SalesReturnRecreationTests.Context();
        var result = await gateway.CreateAsync(context, request.MainCounterpartyId, items, default);
        Assert.Equal(items.Select(x => x.SyncId), result.Select(x => x.SyncId));
        Assert.All(result, x => { Assert.NotNull(x.DocumentId); Assert.Null(x.ErrorCode); });
        Assert.Equal([context.AccountId], limiter.WaitAccounts);
        Assert.Equal(1, limiter.Observations);
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("duplicate", true)]
    [InlineData("wrongAgent", false)]
    [InlineData("oldId", false)]
    [InlineData("error", false)]
    public async Task CreateDoesNotReportMalformedOrRejectedRowsAsSuccess(string scenario, bool retryable)
    {
        var request = SalesReturnRecreationTests.Request();
        var item = new SalesReturnOperationItem { OldDocumentId = request.Documents[0].OldDocumentId, SyncId = Guid.NewGuid(), Payload = "{}" };
        var row = new JsonObject { ["id"] = scenario == "oldId" ? item.OldDocumentId : Guid.NewGuid(), ["syncId"] = item.SyncId,
            ["agent"] = SalesReturnRecreationTests.Reference("counterparty", scenario == "wrongAgent" ? Guid.NewGuid() : request.MainCounterpartyId) };
        var rows = scenario switch
        {
            "missing" => new JsonArray(),
            "duplicate" => new JsonArray(row.DeepClone(), row.DeepClone()),
            "error" => new JsonArray(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["code"] = 3000, ["error"] = "Rejected" }) }),
            _ => new JsonArray(row)
        };
        var gateway = Gateway(_ => Task.FromResult(Response(rows.ToJsonString())));
        var result = Assert.Single(await gateway.CreateAsync(SalesReturnRecreationTests.Context(), request.MainCounterpartyId, [item], default));
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(retryable, result.Retryable);
    }

    [Fact]
    public async Task MixedBatchPreservesSuccessAndRetriesUnattributedFailure()
    {
        var main = Guid.NewGuid();
        var items = Enumerable.Range(0, 2).Select(_ => new SalesReturnOperationItem { SyncId = Guid.NewGuid(), Payload = "{}" }).ToArray();
        var rows = new JsonArray(new JsonObject { ["errors"] = new JsonArray(new JsonObject { ["code"] = 3000 }) },
            new JsonObject { ["id"] = Guid.NewGuid(), ["syncId"] = items[0].SyncId, ["agent"] = SalesReturnRecreationTests.Reference("counterparty", main) });
        var gateway = Gateway(_ => Task.FromResult(Response(rows.ToJsonString())));
        var results = await gateway.CreateAsync(SalesReturnRecreationTests.Context(), main, items, default);
        Assert.NotNull(results[0].DocumentId);
        Assert.Null(results[0].ErrorCode);
        Assert.Null(results[1].DocumentId);
        Assert.True(results[1].Retryable);
    }

    [Theory]
    [InlineData("original")]
    [InlineData("demand")]
    [InlineData("contract")]
    [InlineData("account")]
    public async Task PreflightRejectsWrongOriginalDemandContractOrAccount(string wrong)
    {
        var request = SalesReturnRecreationTests.Request();
        var demandId = Guid.NewGuid();
        var item = request.Documents[0] with { NewAgentAccountId = Guid.NewGuid(), NewContractId = Guid.NewGuid() };
        // Deserialize to preserve the original DTO while adding a demand reference.
        var data = JsonSerializer.SerializeToNode(item.Data, SalesReturnPayloadBuilder.JsonOptions)!.AsObject();
        data["demand"] = SalesReturnRecreationTests.Reference("demand", demandId);
        item = item with { Data = data.Deserialize<MsContractor.Contracts.Internal.SalesReturnCopyData>(SalesReturnPayloadBuilder.JsonOptions)! };
        var gateway = Gateway(message =>
        {
            Assert.Equal(HttpMethod.Get, message.Method);
            var path = message.RequestUri!.AbsolutePath;
            var id = Guid.Parse(path.Split('/').Last());
            var kind = path.Contains("/accounts/") ? "account" : id == demandId ? "demand" : id == item.NewContractId ? "contract" :
                id == item.OldDocumentId ? "original" : "main";
            var agent = kind == "original" ? item.DuplicateCounterpartyId : request.MainCounterpartyId;
            if (kind == wrong) agent = Guid.NewGuid();
            return Task.FromResult(Response(new JsonObject { ["id"] = kind == "account" && wrong == "account" ? Guid.NewGuid() : id,
                ["agent"] = SalesReturnRecreationTests.Reference("counterparty", agent) }.ToJsonString()));
        });
        var error = await Assert.ThrowsAsync<EgressException>(() => gateway.ValidateAsync(SalesReturnRecreationTests.Context(), request.MainCounterpartyId, item, default));
        Assert.Equal("SALESRETURN_PRECONDITION_FAILED", error.Code);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task DeleteAcceptsEmptySuccessAndExistsRecognizes404()
    {
        var id = Guid.NewGuid();
        var gateway = Gateway(message =>
        {
            Assert.EndsWith($"entity/salesreturn/{id}", message.RequestUri!.AbsoluteUri);
            Assert.Null(message.Content);
            return Task.FromResult(new HttpResponseMessage(message.Method == HttpMethod.Delete ? HttpStatusCode.NoContent : HttpStatusCode.NotFound));
        });
        var context = SalesReturnRecreationTests.Context();
        await gateway.DeleteAsync(context, id, default);
        Assert.False(await gateway.ExistsAsync(context, id, default));
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(400, false)]
    [InlineData(403, false)]
    public async Task HttpErrorsHaveControlledRetryPolicy(int status, bool retryable)
    {
        var gateway = Gateway(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("private upstream body") }));
        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.DeleteAsync(SalesReturnRecreationTests.Context(), Guid.NewGuid(), default));
        Assert.Equal(retryable, exception.Retryable);
        Assert.DoesNotContain("private", exception.SafeMessage);
    }

    [Fact]
    public async Task TimeoutIsRetryableButCallerCancellationPropagates()
    {
        var gateway = Gateway(_ => throw new TaskCanceledException());
        Assert.True((await Assert.ThrowsAsync<EgressException>(() => gateway.DeleteAsync(SalesReturnRecreationTests.Context(), Guid.NewGuid(), default))).Retryable);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gateway.DeleteAsync(SalesReturnRecreationTests.Context(), Guid.NewGuid(), cts.Token));
    }

    [Fact]
    public async Task ControllerRejectsMissingAuthenticationAndContextBeforeUsingService()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" }).Build();
        var controller = new InternalSalesReturnsController(config, null!) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>(await controller.RecreateAsync(Guid.NewGuid(), SalesReturnRecreationTests.Request(), default));
        controller.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";
        Assert.IsType<BadRequestObjectResult>(await controller.RecreateAsync(Guid.NewGuid(), SalesReturnRecreationTests.Request(), default));
    }

    private static MoySkladSalesReturnGateway Gateway(Func<HttpRequestMessage, Task<HttpResponseMessage>> action, Limiter? limiter = null) => new(
        new HttpClient(new Handler(action)) { BaseAddress = SalesReturnRecreationTests.BaseUri }, new Tokens(), limiter ?? new Limiter(),
        new MoySkladResponseHandler(NullLogger<MoySkladResponseHandler>.Instance));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request);
    }
    private sealed class Tokens : IVendorTokenClient
    { public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult("test-token"); }
    private sealed class Limiter : IMoySkladRateLimiter
    {
        public List<Guid> WaitAccounts { get; } = [];
        public int Observations { get; private set; }
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); WaitAccounts.Add(accountId); return Task.CompletedTask; }
        public Task ObserveAsync(Guid accountId, MoySkladRateLimitObservation observation, CancellationToken cancellationToken)
        { Observations++; return Task.CompletedTask; }
    }
}
