using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;

namespace MsContractor.Sync.Tests;

public sealed class SalesReturnRecreationEgressClientTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.MultiStatus)]
    public async Task RecreateAsync_PostsExpectedRequestAndAcceptsSuccessStatuses(HttpStatusCode statusCode)
    {
        var accountId = Guid.NewGuid();
        var mainAgentId = Guid.NewGuid();
        var salesReturnIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var mergeJobId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var handler = new RecordingHandler(statusCode, new SalesReturnRecreationResponse(
            operationId,
            mainAgentId,
            salesReturnIds.Select(id => new SalesReturnRecreationDocumentResponse(
                id, Guid.NewGuid(), null, "Completed", "Completed", null, null)).ToArray()));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["InternalApi:Key"] = "test-key" })
            .Build();
        var client = new SalesReturnRecreationEgressClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://egress.test/") }, configuration);

        var response = await client.RecreateAsync(
            accountId, mainAgentId, salesReturnIds, mergeJobId, operationId, userId, correlationId,
            CancellationToken.None);

        Assert.Equal(operationId, response.OperationId);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal($"http://egress.test/internal/accounts/{accountId:D}/documents/salesreturn/recreate",
            handler.Request.RequestUri!.ToString());
        Assert.Equal("test-key", handler.Request.Headers.GetValues(InternalApiHeaders.ApiKey).Single());
        Assert.Equal(mergeJobId.ToString("D"), handler.Request.Headers.GetValues(InternalApiHeaders.MergeJobId).Single());
        Assert.Equal(operationId.ToString("D"), handler.Request.Headers.GetValues(InternalApiHeaders.OperationId).Single());
        Assert.Equal(userId.ToString("D"), handler.Request.Headers.GetValues(InternalApiHeaders.UserId).Single());
        Assert.Equal(correlationId.ToString("D"), handler.Request.Headers.GetValues(InternalApiHeaders.CorrelationId).Single());
        var body = JsonSerializer.Deserialize<SalesReturnRecreationRequest>(
            handler.RequestBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.Equal(mainAgentId, body!.MainAgentId);
        Assert.Equal(salesReturnIds, body.SalesReturnIds);
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        SalesReturnRecreationResponse response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = JsonContent.Create(response)
            };
        }
    }
}
