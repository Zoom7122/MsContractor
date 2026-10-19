using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.BuildingBlocks.Security;
using MsContractor.CatalogSyncService.Controllers;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class InternalDocumentsInOperationControllerTests
{
    [Fact]
    public async Task GetAsync_RequiresInternalApiKey()
    {
        var service = new CapturingDocumentsService();
        var controller = CreateController(service);

        var result = await controller.GetAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<UnauthorizedObjectResult>(result);
        Assert.Null(service.AccountId);
    }

    [Fact]
    public async Task GetAsync_ReturnsRawRowsForAuthorizedAccountAndMergeJob()
    {
        var accountId = Guid.NewGuid();
        var mergeJobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");
        var rows = new[] { new DocumentsInOperationContent("recreate_salesreturn", "[{\"statusCode\":200}]") };
        var service = new CapturingDocumentsService(rows);
        var controller = CreateController(service);
        controller.HttpContext.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";
        controller.HttpContext.Request.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        var result = await controller.GetAsync(accountId, mergeJobId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(rows, ok.Value);
        Assert.Equal(accountId, service.AccountId);
        Assert.Equal(mergeJobId, service.MergeJobId);
        Assert.Equal(correlationId, controller.Response.Headers[InternalApiHeaders.CorrelationId]);
    }

    [Theory]
    [InlineData(true, false, "INVALID_ACCOUNT_ID")]
    [InlineData(false, true, "INVALID_MERGE_JOB_ID")]
    public async Task GetAsync_RejectsEmptyIdentifiers(bool emptyAccount, bool emptyJob, string expectedCode)
    {
        var service = new CapturingDocumentsService();
        var controller = CreateController(service);
        controller.HttpContext.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";

        var result = await controller.GetAsync(
            emptyAccount ? Guid.Empty : Guid.NewGuid(),
            emptyJob ? Guid.Empty : Guid.NewGuid(),
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(expectedCode, Assert.IsType<InternalErrorResponse>(badRequest.Value).Code);
        Assert.Null(service.AccountId);
    }

    private static InternalDocumentsInOperationController CreateController(
        IDocumentsInOperationStateService service) => new(
        service,
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" })
            .Build())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    private sealed class CapturingDocumentsService(
        IReadOnlyList<DocumentsInOperationContent>? response = null) : IDocumentsInOperationStateService
    {
        public Guid? AccountId { get; private set; }
        public Guid? MergeJobId { get; private set; }

        public Task<IReadOnlyList<DocumentsInOperationContent>> GetForMergeJobAsync(
            Guid accountId,
            Guid mergeJobId,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            MergeJobId = mergeJobId;
            return Task.FromResult(response ?? (IReadOnlyList<DocumentsInOperationContent>)Array.Empty<DocumentsInOperationContent>());
        }
    }
}
