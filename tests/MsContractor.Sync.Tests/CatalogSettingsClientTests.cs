using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MsContractor.Contracts.Internal;
using MsContractor.Gateway.Bff.Clients;

namespace MsContractor.Sync.Tests;

public sealed class CatalogSettingsClientTests
{
    [Fact]
    public async Task GetSettingsAsync_CallsAccountScopedEndpointWithInternalApiKey()
    {
        var accountId = Guid.NewGuid();
        var expected = new CatalogSettingsResponse(
            [new CatalogDuplicateExclusionSetting("email", "shared@example.com")],
            new CatalogDuplicateSearchOptions(false),
            new CatalogDuplicateSearchLimits(25, 10));
        string? sentUrl = null;
        string? sentMethod = null;
        string? sentApiKey = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            sentUrl = request.RequestUri?.ToString();
            sentMethod = request.Method.Method;
            sentApiKey = request.Headers.GetValues(InternalApiHeaders.ApiKey).Single();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(expected)
            };
        }))
        {
            BaseAddress = new Uri("http://catalog-sync/")
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Key"] = "test-key"
            })
            .Build();
        var client = new CatalogSyncClient(httpClient, configuration);

        var actual = await client.GetSettingsAsync(accountId, CancellationToken.None);

        Assert.Equal($"http://catalog-sync/internal/accounts/{accountId:D}/settings", sentUrl);
        Assert.Equal(HttpMethod.Get.Method, sentMethod);
        Assert.Equal("test-key", sentApiKey);
        Assert.Collection(actual.DuplicateExclusions,
            exclusion =>
            {
                Assert.Equal("email", exclusion.Field);
                Assert.Equal("shared@example.com", exclusion.Value);
            });
        Assert.False(actual.DuplicateSearchOptions.IncludeArchivedWithDocuments);
        Assert.Equal(25, actual.SearchLimits.GroupLimit);
        Assert.Equal(10, actual.SearchLimits.ItemLimit);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(callback(request));
    }
}
