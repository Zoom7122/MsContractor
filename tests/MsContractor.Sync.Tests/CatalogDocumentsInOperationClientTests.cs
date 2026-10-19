using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;

namespace MsContractor.Sync.Tests;

public sealed class CatalogDocumentsInOperationClientTests
{
    [Fact]
    public async Task GetDocumentsInOperationAsync_UsesScopedEndpointAndInternalHeaders()
    {
        var accountId = Guid.NewGuid();
        var mergeJobId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");
        IReadOnlyList<DocumentsInOperationContent> expected =
        [new("recreate_salesreturn", "[{\"statusCode\":207,\"body\":{}}]")];
        string? sentUrl = null;
        string? sentApiKey = null;
        string? sentCorrelationId = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            sentUrl = request.RequestUri?.ToString();
            sentApiKey = request.Headers.GetValues(InternalApiHeaders.ApiKey).Single();
            sentCorrelationId = request.Headers.GetValues(InternalApiHeaders.CorrelationId).Single();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        }))
        {
            BaseAddress = new Uri("http://catalog-sync/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" })
            .Build();
        var client = new CatalogSyncClient(httpClient, configuration);

        var actual = await client.GetDocumentsInOperationAsync(
            accountId,
            mergeJobId,
            correlationId,
            CancellationToken.None);

        Assert.Equal($"http://catalog-sync/internal/accounts/{accountId:D}/merge-jobs/{mergeJobId:D}/documents-in-operation", sentUrl);
        Assert.Equal("test-key", sentApiKey);
        Assert.Equal(correlationId, sentCorrelationId);
        Assert.Equal(expected, actual);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
