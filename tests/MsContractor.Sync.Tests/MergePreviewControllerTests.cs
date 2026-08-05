using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Duplicates;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergePreviewControllerTests
{
    [Fact]
    public async Task CreateAsync_UsesSessionAccountAndForwardsSelectedFields()
    {
        var accountId = Guid.NewGuid();
        var client = new CapturingClient();
        var controller = Create(new GatewaySession(accountId, Guid.NewGuid()), client);
        controller.ControllerContext.HttpContext.Request.QueryString = new QueryString("?fields=name&fields=phone");

        var result = await controller.CreateAsync(["name", "phone"], CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(accountId, client.AccountId);
        Assert.Equal(["name", "phone"], client.Fields);
    }

    [Fact]
    public async Task CreateAsync_ForwardsMissingFieldsToDuplicatesService()
    {
        var client = new CapturingClient();
        var result = await Create(new GatewaySession(Guid.NewGuid(), Guid.NewGuid()), client)
            .CreateAsync(null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(client.AccountId);
        Assert.Empty(client.Fields!);
    }

    private static MergePreviewController Create(GatewaySession? session, CapturingClient client) => new(
        new FakeSessionReader(session), client,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Session:CookieName"] = "mscontractor.session" }).Build())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private sealed class FakeSessionReader(GatewaySession? session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) => Task.FromResult(session);
    }

    private sealed class CapturingClient : IDuplicatePreviewClient
    {
        public Guid? AccountId { get; private set; }
        public IReadOnlyList<string>? Fields { get; private set; }
        public Task<IReadOnlyList<DuplicateGroupDto>> FindAsync(Guid accountId, IReadOnlyList<string> fields, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            Fields = fields;
            return Task.FromResult<IReadOnlyList<DuplicateGroupDto>>([]);
        }
    }
}
