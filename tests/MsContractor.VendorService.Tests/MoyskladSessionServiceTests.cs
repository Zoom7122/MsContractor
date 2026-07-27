using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Tests;

public sealed class MoyskladSessionServiceTests
{
    [Fact]
    public async Task CreateAsync_CreatesSessionAndUpdatesLastContext()
    {
        var accountId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var repository = new FakeInstallationRepository();
        var installation = ActiveInstallation(accountId);
        repository.Add(installation);
        var contextClient = new FakeContextClient(new MoyskladEmployeeContext(employeeId, accountId));
        var store = new FakeSessionStore();
        var now = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var service = new MoyskladSessionService(
            contextClient,
            store,
            repository,
            TestSupport.Options(),
            now);

        var result = await service.CreateAsync(ValidRequest(), "old-token", CancellationToken.None);

        Assert.Equal(accountId, result.Response.AccountId);
        Assert.Equal(employeeId, result.Response.EmployeeId);
        Assert.Equal("token-1", result.Token);
        Assert.Contains("old-token", store.DeletedTokens);
        Assert.Equal(now.UtcNow, installation.LastContextAt);
        Assert.Equal(1, repository.SaveChangesCalls);
    }

    [Fact]
    public async Task CreateAsync_RejectsUnexpectedApplicationBeforeCallingMoysklad()
    {
        var repository = new FakeInstallationRepository();
        var contextClient = new FakeContextClient(
            new MoyskladEmployeeContext(Guid.NewGuid(), Guid.NewGuid()));
        var service = new MoyskladSessionService(
            contextClient,
            new FakeSessionStore(),
            repository,
            TestSupport.Options(),
            TimeProvider.System);
        var request = ValidRequest() with { AppUid = "another.application" };

        await Assert.ThrowsAsync<VendorForbiddenException>(
            () => service.CreateAsync(request, null, CancellationToken.None));

        Assert.Equal(0, contextClient.Calls);
    }

    [Fact]
    public async Task CreateAsync_RejectsContextFromAccountWithoutActiveInstallation()
    {
        var repository = new FakeInstallationRepository();
        var service = new MoyskladSessionService(
            new FakeContextClient(
                new MoyskladEmployeeContext(Guid.NewGuid(), Guid.NewGuid())),
            new FakeSessionStore(),
            repository,
            TestSupport.Options(),
            TimeProvider.System);

        await Assert.ThrowsAsync<VendorForbiddenException>(
            () => service.CreateAsync(ValidRequest(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Uninstalled")]
    public async Task CreateAsync_RejectsInactiveInstallation(string status)
    {
        var accountId = Guid.NewGuid();
        var repository = new FakeInstallationRepository();
        var installation = ActiveInstallation(accountId);
        installation.Status = status;
        repository.Add(installation);
        var service = new MoyskladSessionService(
            new FakeContextClient(new MoyskladEmployeeContext(Guid.NewGuid(), accountId)),
            new FakeSessionStore(),
            repository,
            TestSupport.Options(),
            TimeProvider.System);

        await Assert.ThrowsAsync<VendorForbiddenException>(
            () => service.CreateAsync(ValidRequest(), null, CancellationToken.None));
    }

    private static MoyskladSessionRequest ValidRequest() =>
        new("context-key", TestSupport.AppId.ToString("D"), TestSupport.AppUid, "ru_RU");

    private static Installation ActiveInstallation(Guid accountId) =>
        new()
        {
            AccountId = accountId,
            AppId = TestSupport.AppId,
            AppUid = TestSupport.AppUid,
            Status = "Active",
            InstalledAt = DateTimeOffset.UtcNow,
            ActivatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}
