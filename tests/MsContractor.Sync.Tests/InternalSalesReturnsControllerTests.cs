using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Services.Documents.Salesreturn;

namespace MsContractor.Sync.Tests;

public sealed class InternalSalesReturnsControllerTests
{
    [Fact]
    public async Task RecreateAsync_InvokesRecreationServiceForRouteAccountAndRequest()
    {
        var accountId = Guid.NewGuid();
        var mainAgentId = Guid.NewGuid();
        var documentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var service = new CapturingRecreationService();
        var controller = Controller(service, authorized: true);

        var result = await controller.RecreateAsync(
            accountId,
            new SalesReturnRecreationRequest(mainAgentId, documentIds),
            CancellationToken.None);

        var response = Assert.IsType<OkObjectResult>(result);
        var body = Assert.IsType<SalesReturnRecreationResult>(response.Value);
        Assert.Equal(accountId, service.AccountId);
        Assert.Equal(mainAgentId, service.MainAgentId);
        Assert.Equal(documentIds, service.DocumentIds);
        Assert.Equal(documentIds, body.TransferredDocumentIds);
        Assert.Equal(documentIds, body.TransferredDocuments.Select(item => item.SourceDocumentId));
        Assert.Equal(body.OperationId.ToString("D"), controller.Response.Headers[InternalApiHeaders.CorrelationId]);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = json.RootElement;
        Assert.True(root.TryGetProperty("transferredDocumentIds", out _));
        Assert.True(root.TryGetProperty("transferredDocuments", out _));
        Assert.True(root.TryGetProperty("skippedDocumentIds", out _));
        Assert.True(root.TryGetProperty("skippedDocuments", out _));
        Assert.True(root.TryGetProperty("createdWithErrors", out _));
        Assert.True(root.TryGetProperty("unCreatedButDeleted", out _));
        Assert.False(root.TryGetProperty("operationId", out _));
        Assert.False(root.TryGetProperty("mainAgentId", out _));
        Assert.False(root.TryGetProperty("documents", out _));
        Assert.Empty(root.GetProperty("skippedDocumentIds").EnumerateArray());
        Assert.Empty(root.GetProperty("skippedDocuments").EnumerateArray());
        Assert.Empty(root.GetProperty("createdWithErrors").EnumerateArray());
        Assert.Empty(root.GetProperty("unCreatedButDeleted").EnumerateArray());
    }

    [Fact]
    public async Task RecreateAsync_RejectsUnauthenticatedOrInvalidRequestsWithoutCallingService()
    {
        var unauthenticatedService = new CapturingRecreationService();
        var unauthenticated = Controller(unauthenticatedService, authorized: false);
        var valid = new SalesReturnRecreationRequest(Guid.NewGuid(), [Guid.NewGuid()]);

        Assert.IsType<UnauthorizedObjectResult>(await unauthenticated.RecreateAsync(
            Guid.NewGuid(), valid, CancellationToken.None));
        Assert.Null(unauthenticatedService.AccountId);

        var invalidService = new CapturingRecreationService();
        var invalid = Controller(invalidService, authorized: true);
        var duplicate = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>(await invalid.RecreateAsync(
            Guid.NewGuid(), new SalesReturnRecreationRequest(Guid.NewGuid(), [duplicate, duplicate]), CancellationToken.None));
        Assert.Null(invalidService.AccountId);
    }

    [Fact]
    public async Task RecreateAsync_ReturnsMultiStatusWhenThereAreSkippedDocuments()
    {
        var skippedId = Guid.NewGuid();
        var service = new CapturingRecreationService
        {
            Result = new SalesReturnRecreationResult(
                [],
                [],
                [skippedId],
                [new SalesReturnSkippedDocumentResult(
                    skippedId, "Skipped", "SALESRETURN_FACTURE_OUT_PRESENT", "The salesreturn was skipped.")],
                [],
                [])
            {
                OperationId = Guid.NewGuid()
            }
        };
        var controller = Controller(service, authorized: true);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new SalesReturnRecreationRequest(Guid.NewGuid(), [Guid.NewGuid()]),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        var body = Assert.IsType<SalesReturnRecreationResult>(response.Value);
        var document = Assert.Single(body.SkippedDocuments);
        Assert.Equal(skippedId, document.DocumentId);
        Assert.Equal("Skipped", document.Status);
        Assert.Equal("SALESRETURN_FACTURE_OUT_PRESENT", document.ErrorCode);
    }

    [Fact]
    public async Task RecreateAsync_ReturnsMultiStatusWhenAnErrorCollectionIsNonEmpty()
    {
        var sourceId = Guid.NewGuid();
        var service = new CapturingRecreationService
        {
            Result = new SalesReturnRecreationResult(
                [],
                [],
                [],
                [],
                [],
                [new SalesReturnUnCreatedButDeletedResult(
                    sourceId, "Failed", "SALESRETURN_NEW_CREATE_FAILED", "Create failed.")])
            {
                OperationId = Guid.NewGuid()
            }
        };
        var controller = Controller(service, authorized: true);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new SalesReturnRecreationRequest(Guid.NewGuid(), [sourceId]),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
    }

    [Fact]
    public async Task RecreateAsync_ReturnsMultiStatusWhenAReplacementWasCreatedWithErrors()
    {
        var sourceId = Guid.NewGuid();
        var service = new CapturingRecreationService
        {
            Result = new SalesReturnRecreationResult(
                [],
                [],
                [],
                [],
                [new SalesReturnCreatedWithErrorResult(
                    sourceId, Guid.NewGuid(), "CreatedWithError", "SALESRETURN_RELATIONS_REATTACH_FAILED", "Reattach failed.")],
                [])
            {
                OperationId = Guid.NewGuid()
            }
        };
        var controller = Controller(service, authorized: true);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new SalesReturnRecreationRequest(Guid.NewGuid(), [sourceId]),
            CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
    }

    private static InternalSalesReturnsController Controller(CapturingRecreationService service, bool authorized)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" }).Build();
        var context = new DefaultHttpContext();
        if (authorized)
            context.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";

        return new InternalSalesReturnsController(service, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingRecreationService : ISalesReturnRecreationOrchestrator
    {
        public Guid? AccountId { get; private set; }
        public Guid? MainAgentId { get; private set; }
        public IReadOnlyCollection<Guid>? DocumentIds { get; private set; }
        public SalesReturnRecreationResult? Result { get; init; }

        public Task<SalesReturnRecreationResult> RecreateAsync(
            Guid accountId,
            Guid mainAgentId,
            IReadOnlyCollection<Guid> salesReturnIds,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            MainAgentId = mainAgentId;
            DocumentIds = salesReturnIds;
            return Task.FromResult(Result ?? new SalesReturnRecreationResult(
                salesReturnIds.ToArray(),
                salesReturnIds.Select(id => new SalesReturnTransferredDocumentResult(id, Guid.NewGuid())).ToArray(),
                [],
                [],
                [],
                [])
            {
                OperationId = Guid.NewGuid()
            });
        }
    }
}
