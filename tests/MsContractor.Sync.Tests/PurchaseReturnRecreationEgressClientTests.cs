using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

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

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
