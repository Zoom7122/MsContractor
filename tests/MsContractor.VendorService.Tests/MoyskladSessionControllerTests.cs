using MsContractor.VendorService.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Controllers;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Tests;

public sealed class MoyskladSessionControllerTests
{
    [Theory]
    [InlineData("Development", false, false, "samesite=lax")]
    [InlineData("Development", true, true, "samesite=none")]
    [InlineData("Production", false, true, "samesite=none")]
    public async Task CreateAsync_IssuesEnvironmentAppropriateHttpOnlyCookie(
        string environmentName,
        bool forwardedHttps,
        bool secure,
        string expectedSameSite)
    {
        var accountId = Guid.NewGuid();
        var employeeId = Guid.NewGuid();
        var repository = new FakeInstallationRepository();
        repository.Add(ActiveInstallation(accountId));
        var service = new MoyskladSessionService(
            new FakeContextClient(new MoyskladEmployeeContext(employeeId, accountId)),
            new FakeSessionStore(),
            repository,
            TestSupport.Options(),
            TimeProvider.System);
        var controller = new MoyskladSessionController(
            service,
            TestSupport.Options(),
            TestSupport.DevSessionOptions(accountId),
            new TestWebHostEnvironment(environmentName),
            TimeProvider.System,
            NullLogger<MoyskladSessionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        if (forwardedHttps)
            controller.Request.Headers["X-Forwarded-Proto"] = "https";

        var result = await controller.CreateAsync(
            new MoyskladSessionRequest(
                "context-key",
                TestSupport.AppId.ToString("D"),
                TestSupport.AppUid,
                "ru_RU"),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var cookie = controller.Response.Headers.SetCookie.ToString().ToLowerInvariant();
        Assert.Contains("mscontractor.session=token-1", cookie);
        Assert.Contains("httponly", cookie);
        Assert.Contains(expectedSameSite, cookie);
        Assert.Contains("path=/", cookie);
        Assert.Equal(secure, cookie.Contains("secure"));
    }

    [Fact]
    public async Task LogoutAsync_IsIdempotentAndClearsCookie()
    {
        var repository = new FakeInstallationRepository();
        var service = new MoyskladSessionService(
            new FakeContextClient(new MoyskladEmployeeContext(Guid.NewGuid(), Guid.NewGuid())),
            new FakeSessionStore(),
            repository,
            TestSupport.Options(),
            TimeProvider.System);
        var controller = new MoyskladSessionController(
            service,
            TestSupport.Options(),
            TestSupport.DevSessionOptions(Guid.NewGuid()),
            new TestWebHostEnvironment(Environments.Development),
            TimeProvider.System,
            NullLogger<MoyskladSessionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.LogoutAsync(CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Contains(
            "mscontractor.session=",
            controller.Response.Headers.SetCookie.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task CreateDevAsync_CreatesSessionForConfiguredAccountWithoutVendorLookups()
    {
        var accountId = Guid.NewGuid();
        var contextClient = new FakeContextClient(
            new MoyskladEmployeeContext(Guid.NewGuid(), Guid.NewGuid()));
        var sessionStore = new FakeSessionStore();
        var repository = new FakeInstallationRepository();
        var service = new MoyskladSessionService(
            contextClient,
            sessionStore,
            repository,
            TestSupport.Options(),
            TimeProvider.System);
        var controller = new MoyskladSessionController(
            service,
            TestSupport.Options(),
            TestSupport.DevSessionOptions(accountId),
            new TestWebHostEnvironment(Environments.Development),
            TimeProvider.System,
            NullLogger<MoyskladSessionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        controller.Request.Headers.Cookie = "mscontractor.session=prior-token";

        var result = await controller.CreateDevAsync(CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(
            new MoyskladSessionResponse(accountId, accountId),
        Assert.IsType<MoyskladSessionResponse>(response.Value));
        Assert.Equal(["prior-token"], sessionStore.DeletedTokens);
        var createdSession = Assert.Single(sessionStore.CreatedSessions);
        Assert.Equal(accountId, createdSession.AccountId);
        Assert.Equal(accountId, createdSession.EmployeeId);
        Assert.Equal(0, contextClient.Calls);
        Assert.Equal(0, repository.SaveChangesCalls);
        Assert.Contains("mscontractor.session=token-1", controller.Response.Headers.SetCookie.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task CreateDevAsync_ReturnsNotFoundOutsideDevelopment()
    {
        var sessionStore = new FakeSessionStore();
        var service = new MoyskladSessionService(
            new FakeContextClient(new MoyskladEmployeeContext(Guid.NewGuid(), Guid.NewGuid())),
            sessionStore,
            new FakeInstallationRepository(),
            TestSupport.Options(),
            TimeProvider.System);
        var controller = new MoyskladSessionController(
            service,
            TestSupport.Options(),
            TestSupport.DevSessionOptions(Guid.NewGuid()),
            new TestWebHostEnvironment(Environments.Production),
            TimeProvider.System,
            NullLogger<MoyskladSessionController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.CreateDevAsync(CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(sessionStore.CreatedSessions);
    }

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
