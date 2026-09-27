using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Controllers;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class InternalSettingsControllerTests
{
    [Fact]
    public async Task GetAsync_RequiresInternalApiKey()
    {
        var service = new CapturingSettingsService();
        var controller = CreateController(service);

        var result = await controller.GetAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(service.AccountId);
    }

    [Fact]
    public async Task GetAsync_ReturnsSettingsForAuthorizedAccount()
    {
        var accountId = Guid.NewGuid();
        var settings = new CatalogSettingsResponse(
            [new CatalogDuplicateExclusionSetting("phone", "+79990000000")],
            new CatalogDuplicateSearchOptions(false),
            new CatalogDuplicateSearchLimits(50, 25));
        var service = new CapturingSettingsService(settings);
        var controller = CreateController(service);
        controller.HttpContext.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";

        var result = await controller.GetAsync(accountId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(accountId, service.AccountId);
        Assert.Same(settings, ok.Value);
    }

    private static InternalSettingsController CreateController(ICatalogSettingsService service) =>
        new(service, new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Key"] = "test-key"
            })
            .Build())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private sealed class CapturingSettingsService(CatalogSettingsResponse? response = null) : ICatalogSettingsService
    {
        public Guid? AccountId { get; private set; }

        public Task<CatalogSettingsResponse> GetAsync(Guid accountId, CancellationToken cancellationToken)
        {
            AccountId = accountId;
            return Task.FromResult(response ?? new CatalogSettingsResponse(
                [],
                new CatalogDuplicateSearchOptions(true),
                new CatalogDuplicateSearchLimits(200, 200)));
        }

        public Task SaveAsync(
            Guid accountId,
            CatalogSettingsRequest? request,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
