using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.Sync.Tests;

public sealed class InternalFactureOutsControllerTests
{
    [Fact]
    public async Task RecreateAsyncReturnsTheCommonDocumentRecreationOutcomeContract()
    {
        var transferredId = Guid.NewGuid();
        var newTransferredId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var unCreatedId = Guid.NewGuid();
        var orchestrator = new CapturingOrchestrator
        {
            Result = new FactureOutRecreationResult(
                [transferredId],
                [new FactureOutSkippedDocumentResult(skippedId, "Skipped", "FACTUREOUT_NOT_READY", "Not ready.")],
                [],
                [])
            {
                TransferredDocuments = [new DocumentRecreationTransferredDocumentResult(transferredId, newTransferredId)],
                UnCreatedButDeleted = [new DocumentRecreationUnCreatedButDeletedResult(
                    unCreatedId, "Failed", "FACTUREOUT_CREATE_FAILED", "The replacement was not created.")]
            }
        };
        var controller = Controller(orchestrator);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new FactureOutRecreationRequest(Guid.NewGuid(), [transferredId, skippedId, unCreatedId]),
            CancellationToken.None);

        var body = Assert.IsType<FactureOutRecreationResponse>(Assert.IsType<AcceptedResult>(result).Value);
        Assert.Equal([transferredId], body.TransferredDocumentIds);
        var mapping = Assert.Single(body.TransferredDocuments);
        Assert.Equal(transferredId, mapping.SourceDocumentId);
        Assert.Equal(newTransferredId, mapping.NewDocumentId);
        Assert.Equal([skippedId], body.SkippedDocumentIds);
        Assert.Single(body.SkippedDocuments);
        var unCreated = Assert.Single(body.UnCreatedButDeleted);
        Assert.Equal(unCreatedId, unCreated.SourceDocumentId);
        Assert.Equal("Failed", unCreated.Status);
        Assert.Empty(body.CreatedWithErrors);
    }

    private static InternalFactureOutsController Controller(CapturingOrchestrator orchestrator)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" }).Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";
        return new InternalFactureOutsController(orchestrator, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingOrchestrator : IFactureOutRecreationOrchestrator
    {
        public FactureOutRecreationResult? Result { get; init; }

        public Task<FactureOutRecreationResult> ExecuteAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result ?? new FactureOutRecreationResult([], [], [], []));
    }
}
