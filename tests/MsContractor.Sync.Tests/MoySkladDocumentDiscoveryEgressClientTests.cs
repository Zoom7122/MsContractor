using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.CatalogSyncService.Clients;
using MsContractor.CatalogSyncService.Models.Exceptions;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class MoySkladDocumentDiscoveryEgressClientTests
{
    [Fact]
    public async Task DiscoverAsync_SendsAccountSelectionAndIdentityHeadersAndReadsDocuments()
    {
        var accountId = Guid.NewGuid();
        var counterpartyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");
        var documentId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var configuration = Configuration();
        var client = new MoySkladDocumentDiscoveryClient(new HttpClient(new CallbackHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal($"/internal/accounts/{accountId:D}/documents/discover", request.RequestUri!.AbsolutePath);
            Assert.Equal("internal-key", Assert.Single(request.Headers.GetValues(InternalApiHeaders.ApiKey)));
            Assert.Equal(userId.ToString("D"), Assert.Single(request.Headers.GetValues(InternalApiHeaders.UserId)));
            Assert.Equal(correlationId, Assert.Single(request.Headers.GetValues(InternalApiHeaders.CorrelationId)));
            var body = await request.Content!.ReadFromJsonAsync<MoySkladDocumentDiscoveryRequest>();
            Assert.Equal(counterpartyIds, body!.CounterpartyIds);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new MoySkladDocumentDiscoveryResponse(
                    [new MoySkladDocumentReference("commissionreportin", documentId, counterpartyIds[0], contractId)],
                    [new MoySkladDocumentTypeCount("commissionreportin", 1)]))
            };
        })) { BaseAddress = new Uri("http://egress.test/") }, configuration);

        var response = await client.DiscoverAsync(
            accountId, counterpartyIds, userId, correlationId, CancellationToken.None);

        var document = Assert.Single(response.Documents);
        Assert.Equal(documentId, document.DocumentId);
        Assert.Equal(counterpartyIds[0], document.CounterpartyId);
        Assert.Equal(contractId, document.ContractId);
        Assert.Equal(1, Assert.Single(response.Counts).Count);
    }

    [Fact]
    public async Task DiscoverAsync_MapsEgressErrorResponse()
    {
        var client = new MoySkladDocumentDiscoveryClient(new HttpClient(new CallbackHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new InternalErrorResponse("EGRESS_BUSY", "Try again later."))
            }))) { BaseAddress = new Uri("http://egress.test/") }, Configuration());

        var exception = await Assert.ThrowsAsync<EgressClientException>(() => client.DiscoverAsync(
            Guid.NewGuid(), [Guid.NewGuid()], Guid.NewGuid(), "correlation", CancellationToken.None));

        Assert.Equal("EGRESS_BUSY", exception.Code);
        Assert.Equal((int)HttpStatusCode.ServiceUnavailable, exception.StatusCode);
    }

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["InternalApi:Key"] = "internal-key"
        })
        .Build();

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
