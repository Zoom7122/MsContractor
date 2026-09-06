using MsContractor.CatalogSyncService.Clients;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;

namespace MsContractor.Sync.Tests;

public sealed class CatalogEgressClientTests
{
    [Fact]
    public async Task GetCounterpartiesAsync_ForwardsArchivedLimitAndOffsetToEgress()
    {
        HttpRequestMessage? captured = null;
        var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"meta":{},"rows":[]}""", Encoding.UTF8, "application/json")
            };
        }))
        {
            BaseAddress = new Uri("http://egress/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Key"] = "internal-key"
            })
            .Build();
        var client = new MoySkladEgressClient(httpClient, configuration);
        var windowFrom = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);
        var windowTo = windowFrom.AddHours(1);

        await client.GetCounterpartiesAsync(
            Guid.NewGuid(),
            true,
            1000,
            2000,
            windowFrom,
            windowTo,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "correlation-id",
            CancellationToken.None);

        Assert.NotNull(captured);
        var uri = captured!.RequestUri!.ToString();
        Assert.Contains("archived=true", uri);
        Assert.Contains("limit=1000", uri);
        Assert.Contains("offset=2000", uri);
        var decodedUri = Uri.UnescapeDataString(uri);
        Assert.Contains($"windowFrom={windowFrom:O}", decodedUri);
        Assert.Contains($"windowTo={windowTo:O}", decodedUri);
        Assert.Equal("internal-key", Assert.Single(captured.Headers.GetValues(InternalApiHeaders.ApiKey)));
        Assert.Equal("correlation-id", Assert.Single(captured.Headers.GetValues(InternalApiHeaders.CorrelationId)));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }
}
