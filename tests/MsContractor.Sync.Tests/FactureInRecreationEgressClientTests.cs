using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.Sync.Tests;

public sealed class FactureInRecreationEgressClientTests
{
    [Fact]
    public async Task RecreateAsync_SendsFactureInsEndpointAndReadsResponse()
    {
        var accountId = Guid.NewGuid();
        var mainCounterpartyId = Guid.NewGuid();
        var factureInIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
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
        var client = new FactureInRecreationEgressClient(
            new HttpClient(new CallbackHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    $"/internal/accounts/{accountId:D}/documents/facturein/recreate",
                    request.RequestUri!.AbsolutePath);
                Assert.Equal("internal-key", request.Headers.GetValues(InternalApiHeaders.ApiKey).Single());
                Assert.Equal(mergeJobId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.MergeJobId).Single());
                Assert.Equal(operationId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.OperationId).Single());
                Assert.Equal(userId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.UserId).Single());
                Assert.Equal(correlationId.ToString("D"), request.Headers.GetValues(InternalApiHeaders.CorrelationId).Single());
                var body = await request.Content!.ReadFromJsonAsync<FactureInRecreationRequest>();
                Assert.Equal(mainCounterpartyId, body!.MainCounterpartyId);
                Assert.Equal(factureInIds, body.FactureInIds);
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = JsonContent.Create(new FactureInRecreationResponse(
                        [factureInIds[0]],
                        [factureInIds[1]],
                        [new FactureInSkippedDocumentResponse(
                            factureInIds[1],
                            "Skipped",
                            "FACTUREIN_BASE_DOCUMENT_MISSING",
                            "Base document was not found.")]))
                };
            }))
            {
                BaseAddress = new Uri("http://egress.test/")
            },
            configuration);

        var response = await client.RecreateAsync(
            accountId,
            mainCounterpartyId,
            factureInIds,
            mergeJobId,
            operationId,
            userId,
            correlationId,
            CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.Accepted, response.StatusCode);
        var parsed = JsonSerializer.Deserialize<FactureInRecreationResponse>(
            response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        Assert.Equal([factureInIds[0]], parsed!.TransferredDocumentIds);
        Assert.Equal([factureInIds[1]], parsed.SkippedDocumentIds);
        var skipped = Assert.Single(parsed.SkippedDocuments!);
        Assert.Equal(factureInIds[1], skipped.DocumentId);
        Assert.Equal("FACTUREIN_BASE_DOCUMENT_MISSING", skipped.ErrorCode);
    }

    [Fact]
    public async Task RecreateAsync_PreservesNonSuccessStatusAndErrorBody()
    {
        const string errorBody = "{\"code\":\"EGRESS_BUSY\",\"message\":\"Try again.\"}";
        var client = new FactureInRecreationEgressClient(
            new HttpClient(new CallbackHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(errorBody)
            }))) { BaseAddress = new Uri("http://egress.test/") },
            new ConfigurationBuilder().Build());

        var response = await client.RecreateAsync(
            Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid()], Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(errorBody, response.Body);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
