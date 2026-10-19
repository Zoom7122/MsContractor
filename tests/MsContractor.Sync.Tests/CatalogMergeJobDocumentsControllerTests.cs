using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Models;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class CatalogMergeJobDocumentsControllerTests
{
    [Fact]
    public async Task GetAsync_RequiresSession()
    {
        var client = new CapturingCatalogStateClient();
        var controller = CreateController(new FakeSessionReader(null), client);

        var result = await controller.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(client.AccountId);
    }

    [Fact]
    public async Task GetAsync_ForwardsAccountFromSessionAndMergeJobId()
    {
        var accountId = Guid.NewGuid();
        var mergeJobId = Guid.NewGuid();
        var rows = new[] { new DocumentsInOperationContent("change_counterparty", "[]") };
        var client = new CapturingCatalogStateClient { Response = rows };
        var controller = CreateController(
            new FakeSessionReader(new GatewaySession(accountId, Guid.NewGuid())),
            client);
        var correlationId = Guid.NewGuid().ToString("D");
        controller.HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName] = correlationId;

        var result = await controller.GetAsync(mergeJobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(rows, ok.Value);
        Assert.Equal(accountId, client.AccountId);
        Assert.Equal(mergeJobId, client.MergeJobId);
        Assert.Equal(correlationId, client.CorrelationId);
    }

    [Fact]
    public async Task GetAsync_MapsCatalogUnavailableTo503()
    {
        var controller = CreateController(
            new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())),
            new CapturingCatalogStateClient { ThrowUnavailable = true });

        var result = await controller.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task GetAsync_RejectsEmptyMergeJobId()
    {
        var client = new CapturingCatalogStateClient();
        var controller = CreateController(new FakeSessionReader(null), client);

        var result = await controller.GetAsync(Guid.Empty, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(client.MergeJobId);
    }

    private static CatalogMergeJobDocumentsController CreateController(
        IGatewaySessionReader sessionReader,
        ICatalogSyncStateClient catalogClient) => new(sessionReader, catalogClient, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Session:CookieName"] = "mscontractor.session"
            })
            .Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class FakeSessionReader(GatewaySession? session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class CapturingCatalogStateClient : ICatalogSyncStateClient
    {
        public Guid? AccountId { get; private set; }
        public Guid? MergeJobId { get; private set; }
        public string? CorrelationId { get; private set; }
        public IReadOnlyList<DocumentsInOperationContent>? Response { get; init; }
        public bool ThrowUnavailable { get; init; }

        public Task<CatalogStateResponse> GetStateAsync(
            Guid accountId,
            string correlationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DocumentsInOperationContent>> GetDocumentsInOperationAsync(
            Guid accountId,
            Guid mergeJobId,
            string correlationId,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            MergeJobId = mergeJobId;
            CorrelationId = correlationId;
            if (ThrowUnavailable)
                throw new CatalogSyncUnavailableException("Unavailable.");

            return Task.FromResult(Response ?? (IReadOnlyList<DocumentsInOperationContent>)Array.Empty<DocumentsInOperationContent>());
        }
    }
}
