using MsContractor.Gateway.Bff.Models;
using MsContractor.Gateway.Bff.Clients;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Merge;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Middleware;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergeJobsControllerTests
{
    [Fact]
    public async Task CreateAsync_UsesOnlySessionTenantContextAndReturnsAccepted()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var client = new CapturingMergeJobsClient();
        var controller = new MergeJobsController(
            new FakeSessionReader(new GatewaySession(accountId, userId)),
            client,
            Configuration())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Items[VendorRequestCorrelationMiddleware.CorrelationIdItemName] = correlationId.ToString("D");
        var request = new CreateMergeJobRequest(
            Guid.NewGuid(), [Guid.NewGuid()],
            new MergeMainCounterpartyDto("Main", null, null, null));

        var result = await controller.CreateAsync(request, CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.IsType<MergeJobAccepted>(accepted.Value);
        Assert.Equal(accountId, client.AccountId);
        Assert.Equal(userId, client.UserId);
        Assert.Equal(correlationId, client.CorrelationId);
        Assert.Same(request, client.Request);
    }

    [Fact]
    public async Task CreateAsync_RequiresSession()
    {
        var client = new CapturingMergeJobsClient();
        var controller = new MergeJobsController(new FakeSessionReader(null), client, Configuration())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.CreateAsync(
            new CreateMergeJobRequest(Guid.NewGuid(), [Guid.NewGuid()], new MergeMainCounterpartyDto("Main", null, null, null)),
            CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(client.AccountId);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Session:CookieName"] = "mscontractor.session" })
        .Build();

    private sealed class FakeSessionReader(GatewaySession? session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class CapturingMergeJobsClient : IMergeJobsClient
    {
        public Guid? AccountId { get; private set; }
        public Guid? UserId { get; private set; }
        public Guid? CorrelationId { get; private set; }
        public CreateMergeJobRequest? Request { get; private set; }

        public Task<MergeJobAccepted> CreateAsync(
            Guid accountId, Guid userId, Guid correlationId, CreateMergeJobRequest request,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            UserId = userId;
            CorrelationId = correlationId;
            Request = request;
            return Task.FromResult(new MergeJobAccepted(Guid.NewGuid(), "pending"));
        }
    }
}
