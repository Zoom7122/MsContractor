using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;
using MsContractor.Gateway.Bff.Controllers;
using MsContractor.Gateway.Bff.Models;
using MsContractor.Gateway.Bff.Models.Exceptions;
using MsContractor.Gateway.Bff.Services;

namespace MsContractor.Sync.Tests;

public sealed class CatalogSettingsControllerTests
{
    [Fact]
    public async Task SaveAsync_RequiresSession()
    {
        var client = new CapturingSettingsClient();
        var controller = CreateController(new FakeSessionReader(null), client);

        var result = await controller.SaveAsync(Settings(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(client.AccountId);
    }

    [Fact]
    public async Task SaveAsync_ForwardsAccountFromSessionAndReturnsNoContent()
    {
        var accountId = Guid.NewGuid();
        var settings = Settings();
        var client = new CapturingSettingsClient();
        var controller = CreateController(
            new FakeSessionReader(new GatewaySession(accountId, Guid.NewGuid())),
            client);

        var result = await controller.SaveAsync(settings, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(accountId, client.AccountId);
        Assert.Same(settings, client.Settings);
    }

    [Fact]
    public async Task SaveAsync_MapsCatalogUnavailableTo503()
    {
        var controller = CreateController(
            new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())),
            new CapturingSettingsClient { ThrowUnavailable = true });

        var result = await controller.SaveAsync(Settings(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task SaveAsync_MapsCatalogValidationFailureTo400()
    {
        var controller = CreateController(
            new FakeSessionReader(new GatewaySession(Guid.NewGuid(), Guid.NewGuid())),
            new CapturingSettingsClient { ThrowRejected = true });

        var result = await controller.SaveAsync(Settings(), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        var error = Assert.IsType<InternalErrorResponse>(badRequest.Value);
        Assert.Equal("INVALID_SEARCH_LIMITS", error.Code);
    }

    private static CatalogSettingsController CreateController(
        IGatewaySessionReader sessionReader,
        ICatalogSettingsClient client) => new(sessionReader, client, Configuration())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Session:CookieName"] = "mscontractor.session"
        })
        .Build();

    private static CatalogSettingsRequest Settings() => new(
        [],
        new CatalogDuplicateSearchOptions(true),
        new CatalogDuplicateSearchLimits(200, 200));

    private sealed class FakeSessionReader(GatewaySession? session) : IGatewaySessionReader
    {
        public Task<GatewaySession?> ReadAsync(string? token, CancellationToken cancellationToken) =>
            Task.FromResult(session);
    }

    private sealed class CapturingSettingsClient : ICatalogSettingsClient
    {
        public Guid? AccountId { get; private set; }
        public CatalogSettingsRequest? Settings { get; private set; }
        public bool ThrowUnavailable { get; init; }
        public bool ThrowRejected { get; init; }

        public Task SaveSettingsAsync(
            Guid accountId,
            CatalogSettingsRequest settings,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            Settings = settings;
            if (ThrowUnavailable)
                throw new CatalogSyncUnavailableException("Unavailable.");
            if (ThrowRejected)
                throw new CatalogSettingsRejectedException("INVALID_SEARCH_LIMITS", "Invalid limits.");
            return Task.CompletedTask;
        }
    }
}
