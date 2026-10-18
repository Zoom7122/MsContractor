using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.Sync.Tests;

public sealed class PurchaseReturnRecreationEgressClientTests
{
    [Fact]
    public async Task RecreateAsync_SendsPurchaseReturnsEndpointAndReadsResponse()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var purchaseReturnIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var mergeJobId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Key"] = "internal-key"
            })
            .Build();
        var client = new PurchaseReturnRecreationEgressClient(
            new HttpClient(new CallbackHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    $"/internal/accounts/{accountId:D}/documents/purchasereturn/recreate",
                    request.RequestUri!.AbsolutePath);
                Assert.Equal("internal-key", request.Headers.GetValues(InternalApiHeaders.ApiKey).Single());
                Assert.Equal(mergeJobId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.MergeJobId).Single());
                Assert.Equal(operationId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.OperationId).Single());
                Assert.Equal(userId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.UserId).Single());
                Assert.Equal(correlationId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.CorrelationId).Single());
                var body = await request.Content!.ReadFromJsonAsync<PurchaseReturnRecreationRequest>();
                Assert.Equal(mainCounterpartyId, body!.MainCounterpartyId);
                Assert.Equal(purchaseReturnIds, body.PurchaseReturnIds);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                Content = JsonContent.Create(new PurchaseReturnRecreationResponse(
                        [purchaseReturnIds[0]],
                        [purchaseReturnIds[1]],
                        [new PurchaseReturnSkippedDocumentResponse(
                            purchaseReturnIds[1],
                            "Skipped",
                            "PURCHASERETURN_SKIPPED",
                            "not ready")]))
                };
            }))
            {
                BaseAddress = new Uri("http://egress.test/")
            },
            configuration);

        var response = await client.RecreateAsync(
            accountId,
            mainCounterpartyId,
            purchaseReturnIds,
            mergeJobId,
            operationId,
            userId,
            correlationId,
            CancellationToken.None);

        Assert.Equal([purchaseReturnIds[0]], response.TransferredDocumentIds);
        Assert.Equal([purchaseReturnIds[1]], response.SkippedDocumentIds);
        var skipped = Assert.Single(response.SkippedDocuments!);
        Assert.Equal(purchaseReturnIds[1], skipped.DocumentId);
        Assert.Equal("PURCHASERETURN_SKIPPED", skipped.ErrorCode);
        Assert.Equal("not ready", skipped.Error);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("server")]
    [InlineData("invalid-json")]
    public async Task RecreateAsync_UnknownResultCannotBeAutomaticallyRetried(string kind)
    {
        var configuration = new ConfigurationBuilder().Build();
        var client = new PurchaseReturnRecreationEgressClient(new HttpClient(new CallbackHandler(_ =>
        {
            if (kind == "transport") throw new HttpRequestException("Connection lost.");
            if (kind == "timeout") throw new OperationCanceledException();
            return Task.FromResult(new HttpResponseMessage(kind == "server" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Accepted)
            { Content = new StringContent(kind == "server" ? "{}" : "invalid-json") });
        })) { BaseAddress = new Uri("http://egress.test/") }, configuration);
        var exception = await Assert.ThrowsAsync<MergeEgressException>(() => client.RecreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(MergeEgressException.PurchaseReturnUnknownResult, exception.Code);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task RecreateAsync_CallerCancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var client = new PurchaseReturnRecreationEgressClient(new HttpClient(new CallbackHandler(_ =>
            throw new OperationCanceledException(cancellation.Token))) { BaseAddress = new Uri("http://egress.test/") },
            new ConfigurationBuilder().Build());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RecreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
        Assert.True(new MergeEgressException("OTHER_OPERATION", "Retryable.", 503).IsRetryable);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
