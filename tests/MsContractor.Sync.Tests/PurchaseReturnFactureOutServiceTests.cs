using System.Text.Json.Nodes;
using MsContractor.MoySkladEgressService.Models;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnFactureOutServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAsync_SkipsInvoiceAndReturnWithMultipleReturns(bool duplicateId)
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var returnId = Guid.NewGuid();
        var invoiceId = fixture.AddInvoice(returnId, duplicateId ? returnId : Guid.NewGuid());
        var result = await fixture.Service.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(),
            [new PurchaseReturnFactureOutRelation(returnId, invoiceId)], CancellationToken.None);
        Assert.Empty(result.PayloadsByPurchaseReturn);
        Assert.Equal("Skipped", Assert.Single(result.SkippedDocuments).Status);
        Assert.Equal(invoiceId, Assert.Single(result.SkippedFactureOuts).DocumentId);
        Assert.Empty(fixture.Gateway.DeletedIds);
        Assert.Empty(fixture.Gateway.CreatedItems);
    }

    [Fact]
    public async Task PrepareAsync_SharedInvoiceSkipsEveryOwnerAndTheirOtherInvoices()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var shared = fixture.AddInvoice(first, second);
        var other = fixture.AddInvoice(first);
        var result = await fixture.Service.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(),
            [new(first, shared), new(second, shared), new(first, other)], CancellationToken.None);
        Assert.Empty(result.PayloadsByPurchaseReturn);
        Assert.Equal(2, result.SkippedDocuments.Count);
        Assert.Equal(2, result.SkippedFactureOuts.Count);
        Assert.Empty(fixture.Gateway.DeletedIds);
    }

    [Fact]
    public async Task PrepareAsync_MissingInvoiceSkipsOnlyItsReturn()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var missing = Guid.NewGuid(); var valid = fixture.AddInvoice(second);
        var result = await fixture.Service.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(),
            [new(first, missing), new(second, valid)], CancellationToken.None);
        Assert.Equal(first, Assert.Single(result.SkippedDocuments).SourceDocumentId);
        Assert.Equal(second, Assert.Single(result.PayloadsByPurchaseReturn).Key);
    }

    [Fact]
    public async Task CreateAsync_RebindsEveryInvoiceAndPreservesSyncIdsAndOtherBases()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var accountId = Guid.NewGuid(); var mainId = Guid.NewGuid();
        var sourceId = Guid.NewGuid(); var newId = Guid.NewGuid();
        var first = fixture.AddInvoice(sourceId); var second = fixture.AddInvoice(sourceId);
        var prepared = await fixture.Service.PrepareAsync(accountId, mainId,
            [new(sourceId, first), new(sourceId, second)], CancellationToken.None);
        var deleted = await fixture.Service.DeleteAsync(accountId, prepared.PayloadsByPurchaseReturn[sourceId], CancellationToken.None);
        Assert.Equal(2, deleted.ReadyToCreatePayloads.Count);
        var result = await fixture.Service.CreateAsync(accountId, mainId, prepared.PayloadsByPurchaseReturn,
            new Dictionary<Guid, Guid> { [sourceId] = newId }, CancellationToken.None);
        Assert.Empty(result.FailedDocuments);
        Assert.Equal(2, result.RecreatedDocuments.Count);
        Assert.Equal(accountId, fixture.Gateway.AccountId);
        foreach (var item in fixture.Gateway.CreatedItems)
        {
            var payload = JsonNode.Parse(item.PayloadJson)!;
            Assert.EndsWith(newId.ToString("D"), payload["returns"]![0]!["meta"]!["href"]!.GetValue<string>());
            Assert.EndsWith(mainId.ToString("D"), payload["agent"]!["meta"]!["href"]!.GetValue<string>());
            var original = prepared.PayloadsByPurchaseReturn[sourceId].Single(p => p.SourceDocumentId == item.SourceDocumentId);
            Assert.Equal(original.NewSyncId, item.SyncId);
            Assert.Equal(JsonNode.Parse(original.PayloadJson)!["demands"]!.ToJsonString(), payload["demands"]!.ToJsonString());
        }
    }

    [Fact]
    public async Task CreateAsync_RetryExceptionKeepsConfirmedIdsAndReportsCreatedWithError()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var sourceId = Guid.NewGuid();
        var first = fixture.AddInvoice(sourceId); var second = fixture.AddInvoice(sourceId);
        fixture.Gateway.RejectedCreateIds.Add(second);
        fixture.Gateway.FailingCreateAttempt = 2;
        var prepared = await fixture.Service.PrepareAsync(Guid.NewGuid(), Guid.NewGuid(),
            [new(sourceId, first), new(sourceId, second)], CancellationToken.None);
        var result = await fixture.Service.CreateAsync(Guid.NewGuid(), Guid.NewGuid(), prepared.PayloadsByPurchaseReturn,
            new Dictionary<Guid, Guid> { [sourceId] = Guid.NewGuid() }, CancellationToken.None);
        Assert.Equal(first, Assert.Single(result.RecreatedDocuments).SourceDocumentId);
        Assert.Equal("CreatedWithError", Assert.Single(result.FailedDocuments).Status);
        Assert.True(fixture.Repository.Created.ContainsKey(first));
        Assert.Contains(second, fixture.Repository.Failed);
        Assert.Equal(2, fixture.Gateway.CreatedBatches.Count);
    }

    [Fact]
    public async Task CreateAsync_WrongVerifiedBasisKeepsMappingAndFailsParent()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var sourceId = Guid.NewGuid(); var mainId = Guid.NewGuid();
        var invoiceId = fixture.AddInvoice(sourceId);
        fixture.Gateway.InvalidVerifiedBasis = true;
        var prepared = await fixture.Service.PrepareAsync(Guid.NewGuid(), mainId,
            [new(sourceId, invoiceId)], CancellationToken.None);
        var result = await fixture.Service.CreateAsync(Guid.NewGuid(), mainId, prepared.PayloadsByPurchaseReturn,
            new Dictionary<Guid, Guid> { [sourceId] = Guid.NewGuid() }, CancellationToken.None);
        Assert.Single(result.RecreatedDocuments);
        Assert.Equal("CreatedWithError", Assert.Single(result.FailedDocuments).Status);
    }
    [Fact]
    public async Task CreateAsync_PersistenceFailureDoesNotLoseConfirmedNewIds()
    {
        var fixture = new PurchaseReturnFactureOutFixture();
        var sourceId = Guid.NewGuid(); var mainId = Guid.NewGuid();
        var invoiceId = fixture.AddInvoice(sourceId);
        var prepared = await fixture.Service.PrepareAsync(Guid.NewGuid(), mainId,
            [new(sourceId, invoiceId)], CancellationToken.None);
        fixture.Repository.SaveException = new InvalidOperationException("Could not save.");
        var result = await fixture.Service.CreateAsync(Guid.NewGuid(), mainId, prepared.PayloadsByPurchaseReturn,
            new Dictionary<Guid, Guid> { [sourceId] = Guid.NewGuid() }, CancellationToken.None);
        Assert.Equal(invoiceId, Assert.Single(result.RecreatedDocuments).SourceDocumentId);
        Assert.Equal("CreatedWithError", Assert.Single(result.FailedDocuments).Status);
    }

}
