using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using MsContractor.Contracts.Sync;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class GatewaySyncControllerTests
{
    [Fact]
    public async Task CreateAsync_UsesAccountAndEmployeeFromSession()
    {
        var accountId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var client = new CapturingCatalogSyncClient();
        var controller = new SyncController(
            new FakeSessionReader(new GatewaySession(accountId, employeeId)),
            client,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Session:CookieName"] = "mscontractor.session" }).Build())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Headers.Cookie = "mscontractor.session=opaque-browser-token";

        var result = await controller.CreateAsync(CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.IsType<SyncAccepted>(accepted.Value);
        Assert.NotNull(client.Request);
        Assert.Equal(accountId, client.Request!.AccountId);
        Assert.Equal(employeeId, client.Request.RequestedByUserId);
        Assert.Equal(SyncMode.Full, client.Request.Mode);
    }

    [Fact]
    public async Task CreateIncrementalAsync_PublishesIncrementalMode()
    {
        var client = new CapturingCatalogSyncClient();
        var controller = new SyncController(
            new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())),
            client,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.CreateIncrementalAsync(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(SyncMode.Incremental, client.Request?.Mode);
    }

    [Fact]
    public void SyncRequested_WithoutModeDefaultsToFull()
    {
        var command = JsonSerializer.Deserialize<SyncRequested>(
            $$"""{"messageId":"{{Guid.NewGuid()}}","syncRunId":"{{Guid.NewGuid()}}","accountId":"{{Guid.NewGuid()}}","requestedByUserId":"{{Guid.NewGuid()}}","requestedAt":"2026-03-20T10:00:00Z"}""");

        Assert.Equal(SyncMode.Full, command?.Mode);
    }

    private sealed class FakeSessionReader(GatewaySession session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult<GatewaySession?>(session);
    }

    private sealed class CapturingCatalogSyncClient : ICatalogSyncClient
    {
        public SyncStartRequest? Request { get; private set; }

        public Task<SyncAccepted> StartAsync(SyncStartRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new SyncAccepted(Guid.NewGuid(), "queued"));
        }
    }
}
