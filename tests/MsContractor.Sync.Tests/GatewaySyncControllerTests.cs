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
        var publisher = new CapturingPublisher();
        var controller = new SyncController(
            new FakeSessionReader(new GatewaySession(accountId, employeeId)),
            publisher,
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["Session:CookieName"] = "mscontractor.session" }).Build(),
            TimeProvider.System)
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
        Assert.NotNull(publisher.Command);
        Assert.Equal(accountId, publisher.Command!.AccountId);
        Assert.Equal(employeeId, publisher.Command.RequestedByUserId);
        Assert.NotEqual(Guid.Empty, publisher.Command.MessageId);
        Assert.NotEqual(Guid.Empty, publisher.Command.SyncRunId);
        Assert.Equal(SyncMode.Full, publisher.Command.Mode);
    }

    [Fact]
    public async Task CreateIncrementalAsync_PublishesIncrementalMode()
    {
        var publisher = new CapturingPublisher();
        var controller = new SyncController(
            new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())),
            publisher,
            new ConfigurationBuilder().Build(),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.CreateIncrementalAsync(CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        Assert.Equal(SyncMode.Incremental, publisher.Command?.Mode);
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

    private sealed class CapturingPublisher : ISyncCommandPublisher
    {
        public SyncRequested? Command { get; private set; }

        public Task PublishAsync(SyncRequested command, CancellationToken cancellationToken)
        {
            Command = command;
            return Task.CompletedTask;
        }
    }
}
