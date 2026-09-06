using MsContractor.VendorService.Clients;
using MsContractor.VendorService.Models.Exceptions;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.VendorService.Services;

namespace MsContractor.VendorService.Tests;

public sealed class MoyskladContextClientTests
{
    [Fact]
    public async Task GetAsync_SendsSignedPostAndReadsMinimalContext()
    {
        var employeeId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        HttpRequestMessage? captured = null;
        string? acceptEncoding = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            acceptEncoding = request.Headers.AcceptEncoding.SingleOrDefault()?.Value;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{employeeId}}","accountId":"{{accountId}}","name":"Not exposed"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        });
        var client = CreateClient(handler);

        var result = await client.GetAsync(
            "context key",
            TestSupport.AppId,
            TestSupport.AppUid,
            CancellationToken.None);

        Assert.Equal(employeeId, result.Id);
        Assert.Equal(accountId, result.AccountId);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.False(string.IsNullOrWhiteSpace(captured.Headers.Authorization?.Parameter));
        Assert.Contains("context/context key", captured.RequestUri!.ToString());
        Assert.Contains($"appId={TestSupport.AppId:D}", captured.RequestUri.ToString());
        Assert.Contains($"appUid={TestSupport.AppUid}", captured.RequestUri.ToString());
        Assert.Equal("gzip", acceptEncoding);
        Assert.Null(captured.Content);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, typeof(VendorForbiddenException))]
    [InlineData(HttpStatusCode.NotFound, typeof(VendorContextExpiredException))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(VendorUpstreamException))]
    public async Task GetAsync_MapsUpstreamErrors(HttpStatusCode status, Type exceptionType)
    {
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(status)));

        var exception = await Record.ExceptionAsync(() => client.GetAsync(
            "key",
            TestSupport.AppId,
            TestSupport.AppUid,
            CancellationToken.None));

        Assert.IsType(exceptionType, exception);
    }

    private static MoyskladContextClient CreateClient(HttpMessageHandler handler)
    {
        var timeProvider = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-27T12:00:00Z"));
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://apps-api.moysklad.ru/api/vendor/1.0/")
        };
        return new MoyskladContextClient(
            httpClient,
            new MoyskladVendorJwtFactory(TestSupport.Options(), timeProvider),
            NullLogger<MoyskladContextClient>.Instance);
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
