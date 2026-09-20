using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Services.Documents.Purchasereturn;

namespace MsContractor.Sync.Tests;

public sealed class InternalPurchaseReturnsControllerTests
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
            new PurchaseReturnRecreationRequest(mainCounterpartyId, documentIds),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        Assert.Null(accepted.Value);
        Assert.Equal(accountId, orchestrator.AccountId);
        Assert.Equal(mainCounterpartyId, orchestrator.MainCounterpartyId);
        Assert.Equal(documentIds, orchestrator.DocumentIds);
    }

    [Fact]
    public async Task RecreateAsync_RejectsUnauthenticatedOrInvalidRequestsWithoutCallingOrchestrator()
    {
        var valid = new PurchaseReturnRecreationRequest(Guid.NewGuid(), [Guid.NewGuid()]);

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

        var invalidRequestOrchestrator = new CapturingOrchestrator();
        var invalidRequest = Controller(invalidRequestOrchestrator, authorized: true);
        var duplicate = Guid.NewGuid();
        Assert.IsType<BadRequestObjectResult>(await invalidRequest.RecreateAsync(
            Guid.NewGuid(),
            new PurchaseReturnRecreationRequest(Guid.NewGuid(), [duplicate, duplicate]),
            CancellationToken.None));
        Assert.Null(invalidRequestOrchestrator.AccountId);
    }

    [Fact]
    public async Task RecreateAsync_RejectsMissingEmptyOrOversizedDocumentList()
    {
        var cases = new PurchaseReturnRecreationRequest?[]
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

    private static InternalPurchaseReturnsController Controller(
        CapturingOrchestrator orchestrator,
        bool authorized)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" }).Build();
        var context = new DefaultHttpContext();
        if (authorized)
            context.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";

        return new InternalPurchaseReturnsController(orchestrator, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingOrchestrator : IPurchaseReturnRecreationOrchestrator
    {
        public Guid? AccountId { get; private set; }
        public Guid? MainCounterpartyId { get; private set; }
        public IReadOnlyList<Guid>? DocumentIds { get; private set; }

        public Task<PurchaseReturnVerificationResult> ExecuteAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            IReadOnlyList<Guid> purchaseReturnIds,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            MainCounterpartyId = mainCounterpartyId;
            DocumentIds = purchaseReturnIds;
            return Task.FromResult(new PurchaseReturnVerificationResult([]));
        }
    }
}
