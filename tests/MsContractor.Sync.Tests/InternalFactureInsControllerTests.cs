using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Services.Documents.Facturein;

namespace MsContractor.Sync.Tests;

public sealed class InternalFactureInsControllerTests
{
    [Fact]
    public async Task RecreateAsync_InvokesOrchestratorForRouteAccountAndRequest()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var documentIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var orchestrator = new CapturingOrchestrator();
        var controller = Controller(orchestrator, authorized: true);

        var result = await controller.RecreateAsync(
            accountId,
            new FactureInRecreationRequest(mainCounterpartyId, documentIds),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<FactureInRecreationResponse>(accepted.Value);
        Assert.Empty(body.TransferredDocumentIds);
        Assert.Empty(body.SkippedDocumentIds);
        Assert.Empty(body.SkippedDocuments!);
        Assert.Empty(body.CreatedWithErrors!);
        Assert.Empty(body.FailedDocuments!);
        Assert.Equal(accountId, orchestrator.AccountId);
        Assert.Equal(mainCounterpartyId, orchestrator.MainCounterpartyId);
        Assert.Equal(documentIds, orchestrator.DocumentIds);
    }

    [Fact]
    public async Task RecreateAsync_ReturnsTransferredSkippedAndRebindErrorsSeparately()
    {
        var transferredId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var failedSourceId = Guid.NewGuid();
        var failedNewId = Guid.NewGuid();
        var creationFailedSourceId = Guid.NewGuid();
        var creationSyncId = Guid.NewGuid();
        var orchestrator = new CapturingOrchestrator
        {
            Result = new FactureInRecreationResult(
                [transferredId],
                [new FactureInSkippedDocumentResult(
                    skippedId,
                    "Skipped",
                    "FACTUREIN_SKIPPED",
                    "source supply is not available")],
                [new FactureInCreatedWithErrorResult(
                    failedSourceId,
                    failedNewId,
                    "CreatedWithError",
                    "FACTUREIN_RELATIONS_REATTACH_FAILED",
                    "related payment could not be rebound")],
                [new FactureInFailedDocumentResult(
                    creationFailedSourceId,
                    creationSyncId,
                    null,
                    "Failed",
                    "FACTUREIN_CREATE_FAILED",
                    "MoySklad rejected facturein creation.")])
        };
        var controller = Controller(orchestrator, authorized: true);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new FactureInRecreationRequest(Guid.NewGuid(), [transferredId, skippedId, failedSourceId]),
            CancellationToken.None);

        var response = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<FactureInRecreationResponse>(response.Value);
        Assert.Equal([transferredId], body.TransferredDocumentIds);
        Assert.Equal([skippedId], body.SkippedDocumentIds);
        var skipped = Assert.Single(body.SkippedDocuments!);
        Assert.Equal(skippedId, skipped.DocumentId);
        Assert.Equal("FACTUREIN_SKIPPED", skipped.ErrorCode);
        Assert.Equal("source supply is not available", skipped.Error);
        var createdWithError = Assert.Single(body.CreatedWithErrors!);
        Assert.Equal(failedSourceId, createdWithError.SourceDocumentId);
        Assert.Equal(failedNewId, createdWithError.NewDocumentId);
        Assert.Equal("FACTUREIN_RELATIONS_REATTACH_FAILED", createdWithError.ErrorCode);
        Assert.DoesNotContain(failedSourceId, body.TransferredDocumentIds);
        var creationFailed = Assert.Single(body.FailedDocuments!);
        Assert.Equal(creationFailedSourceId, creationFailed.SourceDocumentId);
        Assert.Equal(creationSyncId, creationFailed.NewSyncId);
        Assert.Null(creationFailed.NewDocumentId);
        Assert.DoesNotContain(creationFailedSourceId, body.TransferredDocumentIds);
    }

    [Fact]
    public async Task RecreateAsync_ConvertsSkippedExceptionToTextWithoutSerializingException()
    {
        var skippedId = Guid.NewGuid();
        var orchestrator = new CapturingOrchestrator
        {
            Result = new FactureInRecreationResult(
                [],
                [new FactureInSkippedDocumentResult(
                    skippedId,
                    "Skipped",
                    "FACTUREIN_SKIPPED",
                    null,
                    new InvalidOperationException("facturein source is invalid"))],
                [],
                [])
        };
        var controller = Controller(orchestrator, authorized: true);

        var result = await controller.RecreateAsync(
            Guid.NewGuid(),
            new FactureInRecreationRequest(Guid.NewGuid(), [skippedId]),
            CancellationToken.None);

        var body = Assert.IsType<FactureInRecreationResponse>(Assert.IsType<AcceptedResult>(result).Value);
        var skipped = Assert.Single(body.SkippedDocuments!);
        Assert.Equal("facturein source is invalid", skipped.Error);
    }

    [Fact]
    public async Task RecreateAsync_RejectsUnauthenticatedOrInvalidRequestsWithoutCallingOrchestrator()
    {
        var valid = new FactureInRecreationRequest(Guid.NewGuid(), [Guid.NewGuid()]);

        var unauthenticatedOrchestrator = new CapturingOrchestrator();
        var unauthenticated = Controller(unauthenticatedOrchestrator, authorized: false);
        Assert.IsType<UnauthorizedObjectResult>(await unauthenticated.RecreateAsync(
            Guid.NewGuid(), valid, CancellationToken.None));
        Assert.Null(unauthenticatedOrchestrator.AccountId);

        var invalidAccountOrchestrator = new CapturingOrchestrator();
        var invalidAccount = Controller(invalidAccountOrchestrator, authorized: true);
        Assert.IsType<BadRequestObjectResult>(await invalidAccount.RecreateAsync(
            Guid.Empty, valid, CancellationToken.None));
        Assert.Null(invalidAccountOrchestrator.AccountId);

        var duplicate = Guid.NewGuid();
        var invalidRequestOrchestrator = new CapturingOrchestrator();
        var invalidRequest = Controller(invalidRequestOrchestrator, authorized: true);
        Assert.IsType<BadRequestObjectResult>(await invalidRequest.RecreateAsync(
            Guid.NewGuid(),
            new FactureInRecreationRequest(Guid.NewGuid(), [duplicate, duplicate]),
            CancellationToken.None));
        Assert.Null(invalidRequestOrchestrator.AccountId);
    }

    [Fact]
    public async Task RecreateAsync_RejectsMissingEmptyMalformedAndOversizedRequests()
    {
        var cases = new FactureInRecreationRequest?[]
        {
            null,
            new(Guid.NewGuid(), null),
            new(Guid.NewGuid(), []),
            new(Guid.Empty, [Guid.NewGuid()]),
            new(Guid.NewGuid(), [Guid.Empty]),
            new(Guid.NewGuid(), Enumerable.Range(0, 100_001)
                .Select(_ => Guid.NewGuid())
                .ToArray()),
        };

        foreach (var request in cases)
        {
            var orchestrator = new CapturingOrchestrator();
            var controller = Controller(orchestrator, authorized: true);
            var result = await controller.RecreateAsync(
                Guid.NewGuid(), request, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Null(orchestrator.AccountId);
        }
    }

    [Fact]
    public async Task RecreateAsync_MapsKnownFailuresToInternalResponses()
    {
        var cases = new (Exception Exception, int StatusCode, string Code)[]
        {
            (new EgressException(503, "EGRESS_UNAVAILABLE", "egress unavailable"), 503, "EGRESS_UNAVAILABLE"),
            (new InvalidOperationException("invalid source"), 400, "FACTUREIN_PREPARATION_FAILED"),
        };

        foreach (var @case in cases)
        {
            var orchestrator = new CapturingOrchestrator { Exception = @case.Exception };
            var controller = Controller(orchestrator, authorized: true);
            var result = await controller.RecreateAsync(
                Guid.NewGuid(),
                new FactureInRecreationRequest(Guid.NewGuid(), [Guid.NewGuid()]),
                CancellationToken.None);

            var response = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(@case.StatusCode, response.StatusCode);
            var error = Assert.IsType<InternalErrorResponse>(response.Value);
            Assert.Equal(@case.Code, error.Code);
        }
    }

    private static InternalFactureInsController Controller(
        CapturingOrchestrator orchestrator,
        bool authorized)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" }).Build();
        var context = new DefaultHttpContext();
        if (authorized)
            context.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";

        return new InternalFactureInsController(orchestrator, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingOrchestrator : IFactureInRecreationOrchestrator
    {
        public Guid? AccountId { get; private set; }
        public Guid? MainCounterpartyId { get; private set; }
        public IReadOnlyList<Guid>? DocumentIds { get; private set; }
        public FactureInRecreationResult? Result { get; init; }
        public Exception? Exception { get; init; }

        public Task<FactureInRecreationResult> ExecuteAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<Guid> factureInIds,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            MainCounterpartyId = mainCounterpartyId;
            DocumentIds = factureInIds;
            if (Exception is not null)
                return Task.FromException<FactureInRecreationResult>(Exception);

            return Task.FromResult(Result ?? new FactureInRecreationResult([], [], [], []));
        }
    }
}
