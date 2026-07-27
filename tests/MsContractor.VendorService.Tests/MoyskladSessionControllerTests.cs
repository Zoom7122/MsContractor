using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using MsContractor.VendorService.Contracts;
using MsContractor.VendorService.Controllers;
using MsContractor.VendorService.Repo;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Tests;

public sealed class MoyskladSessionControllerTests
{
    [Theory]
    [InlineData("Development", false, "samesite=lax")]
    [InlineData("Production", true, "samesite=none")]
    public async Task CreateAsync_IssuesEnvironmentAppropriateHttpOnlyCookie(
        string environmentName,
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
            new TestWebHostEnvironment(environmentName),
            TimeProvider.System)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

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
            new TestWebHostEnvironment(Environments.Development),
            TimeProvider.System)
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
