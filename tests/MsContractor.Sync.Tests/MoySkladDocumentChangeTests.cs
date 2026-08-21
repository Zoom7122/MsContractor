using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.Sync.Tests;

public sealed class MoySkladDocumentChangeOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("customerorder,customerorder")]
    [InlineData("customerorder,unknown")]
    public void Parse_RejectsMissingDuplicateAndUnsupportedValues(string? value) =>
        Assert.Throws<InvalidOperationException>(() => MoySkladDocumentChangeOptions.Parse(value));

    [Fact]
    public void Parse_PreservesConfiguredOrder()
    {
        var options = MoySkladDocumentChangeOptions.Parse("demand,customerorder");
        Assert.Equal(["demand", "customerorder"], options.DocumentTypes);
    }
}

public sealed class MoySkladDocumentChangeGatewayTests
{
    [Theory]
    [InlineData(1, "PUT")]
    [InlineData(2, "POST")]
    [InlineData(999, "POST")]
    [InlineData(1000, "POST")]
    public async Task ChangeCounterpartyAsync_UsesExpectedMethodAndPayload(int count, string expectedMethod)
    {
        var mainId = Guid.NewGuid();
        var documents = Documents("customerorder", count);
        var requests = new List<(string Method, string Path, int BodyCount)>();
        var gateway = Gateway(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            var bodyItems = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement.EnumerateArray().ToArray()
                : [json.RootElement];
            Assert.All(bodyItems, item => Assert.EndsWith(
                $"/entity/counterparty/{mainId:D}",
                item.GetProperty("agent").GetProperty("meta").GetProperty("href").GetString()));
            requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, bodyItems.Length));
            return JsonResponse(count == 1
                ? Changed(documents[0], mainId)
                : documents.Select(item => Changed(item, mainId)).ToArray());
        });

        var result = await gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            mainId, "customerorder", documents, CancellationToken.None);

        Assert.Equal(count, result.ChangedDocuments.Count);
        Assert.Empty(result.Failures);
        Assert.Single(requests);
        Assert.Equal(expectedMethod, requests[0].Method);
        Assert.Equal(count, requests[0].BodyCount);
        Assert.Equal(count == 1
            ? $"/api/remap/1.2/entity/customerorder/{documents[0].DocumentId:D}"
            : "/api/remap/1.2/entity/customerorder/batch", requests[0].Path);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_ParsesMixedBatchResponse()
    {
        var mainId = Guid.NewGuid();
        var documents = Documents("demand", 2);
        var gateway = Gateway((_, _) => Task.FromResult(JsonResponse(new object[]
        {
            Changed(documents[0], mainId),
            new { errors = new[] { new { code = 3008, error = "Документ нельзя изменить", parameter = "agent" } } }
        })));

        var result = await gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            mainId, "demand", documents, CancellationToken.None);

        Assert.Equal([documents[0]], result.ChangedDocuments);
        var failure = Assert.Single(result.Failures);
        Assert.Equal("MOYSKLAD_3008", failure.Code);
        Assert.Contains("Документ нельзя изменить", failure.Message);
        Assert.False(failure.Retryable);
    }

    private static MoySkladDocumentGateway Gateway(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) =>
        new(
            new HttpClient(new AsyncHandler(callback))
            {
                BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
            },
            new TokenClient(),
            new RateLimiter(),
            NullLogger<MoySkladDocumentGateway>.Instance);

    private static MoySkladDocumentChangeItem[] Documents(string type, int count) =>
        Enumerable.Range(0, count)
            .Select(_ => new MoySkladDocumentChangeItem(type, Guid.NewGuid()))
            .ToArray();

    private static object Changed(MoySkladDocumentChangeItem item, Guid mainId) => new
    {
        id = item.DocumentId,
        meta = new { type = item.DocumentType },
        agent = new
        {
            meta = new
            {
                type = "counterparty",
                href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{mainId:D}"
            }
        }
    };

    private static HttpResponseMessage JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class TokenClient : IVendorTokenClient
    {
        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult("secret-token");
    }

    private sealed class RateLimiter : IMoySkladRateLimiter
    {
        public Task WaitAsync(Guid accountId, Guid? userId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ObserveAsync(Guid accountId, HttpResponseMessage response, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

public sealed class MoySkladDocumentChangeServiceTests
{
    [Theory]
    [InlineData(1001, 2, 1000, 1)]
    [InlineData(2000, 2, 1000, 1000)]
    [InlineData(2001, 3, 1000, 1000, 1)]
    public async Task ChangeCounterpartyAsync_ChunksPerType(
        int count, int expectedCalls, params int[] expectedChunkSizes)
    {
        var gateway = new RecordingGateway();
        var service = Service(gateway, "customerorder");
        var documents = Enumerable.Range(0, count)
            .Select(_ => new MoySkladDocumentChangeItem("customerorder", Guid.NewGuid()))
            .ToArray();

        var response = await service.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            new MoySkladDocumentChangeCounterpartyRequest(Guid.NewGuid(), documents), CancellationToken.None);

        Assert.Equal(count, response.ChangedCount);
        Assert.Equal(expectedCalls, gateway.Calls.Count);
        Assert.Equal(expectedChunkSizes, gateway.Calls.Select(item => item.Documents.Count));
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_ContinuesAfterFailureAndSkipsUnconfiguredType()
    {
        var demand = new MoySkladDocumentChangeItem("demand", Guid.NewGuid());
        var order = new MoySkladDocumentChangeItem("customerorder", Guid.NewGuid());
        var skipped = new MoySkladDocumentChangeItem("invoiceout", Guid.NewGuid());
        var gateway = new RecordingGateway
        {
            Callback = call => call.DocumentType == "demand"
                ? throw new EgressException(429, "MOYSKLAD_RATE_LIMITED", "code=429, error=limit")
                : new MoySkladDocumentChangeChunkResult(call.Documents, [])
        };
        var service = Service(gateway, "demand", "customerorder");

        var response = await service.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            new MoySkladDocumentChangeCounterpartyRequest(Guid.NewGuid(), [order, skipped, demand]),
            CancellationToken.None);

        Assert.Equal(2, gateway.Calls.Count);
        Assert.Equal(["demand", "customerorder"], gateway.Calls.Select(item => item.DocumentType));
        Assert.Single(response.ChangedDocuments);
        Assert.Single(response.SkippedDocuments);
        var failure = Assert.Single(response.Failures);
        Assert.True(failure.Retryable);
        Assert.Contains("error=limit", failure.Message);
    }

    private static MoySkladDocumentChangeService Service(RecordingGateway gateway, params string[] types) =>
        new(gateway, new MoySkladDocumentChangeOptions { DocumentTypes = types },
            NullLogger<MoySkladDocumentChangeService>.Instance);

    private sealed class RecordingGateway : IMoySkladDocumentGateway
    {
        public List<MutationCall> Calls { get; } = [];
        public Func<MutationCall, MoySkladDocumentChangeChunkResult>? Callback { get; init; }

        public Task<MoySkladDocumentChangeChunkResult> ChangeCounterpartyAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId,
            string correlationId, Guid mainCounterpartyId, string documentType,
            IReadOnlyList<MoySkladDocumentChangeItem> documents, CancellationToken cancellationToken)
        {
            var call = new MutationCall(documentType, documents.ToArray());
            Calls.Add(call);
            return Task.FromResult(Callback?.Invoke(call) ?? new MoySkladDocumentChangeChunkResult(documents, []));
        }

        public Task<MoySkladDocumentPage> GetPageAsync(
            Guid accountId, Guid requestedByUserId, string correlationId, string documentType,
            IReadOnlyList<Guid> counterpartyIds, int limit, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record MutationCall(string DocumentType, IReadOnlyList<MoySkladDocumentChangeItem> Documents);
}

public sealed class InternalDocumentChangeControllerTests
{
    [Fact]
    public async Task ChangeCounterpartyAsync_ReturnsMultiStatusAndForwardsMergeContext()
    {
        var service = new CapturingChangeService { HasFailure = true };
        var controller = Controller(service, includeContext: true);
        var request = new MoySkladDocumentChangeCounterpartyRequest(
            Guid.NewGuid(), [new MoySkladDocumentChangeItem("demand", Guid.NewGuid())]);

        var result = await controller.ChangeCounterpartyAsync(Guid.NewGuid(), request, CancellationToken.None);

        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        Assert.NotEqual(Guid.Empty, service.MergeJobId);
        Assert.NotEqual(Guid.Empty, service.OperationId);
        Assert.True(Guid.TryParse(service.CorrelationId, out _));
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_RejectsMissingContextAndDuplicateDocuments()
    {
        var id = Guid.NewGuid();
        var missingContext = Controller(new CapturingChangeService(), includeContext: false);
        var valid = new MoySkladDocumentChangeCounterpartyRequest(
            Guid.NewGuid(), [new MoySkladDocumentChangeItem("demand", id)]);
        Assert.IsType<BadRequestObjectResult>(
            await missingContext.ChangeCounterpartyAsync(Guid.NewGuid(), valid, CancellationToken.None));

        var duplicate = Controller(new CapturingChangeService(), includeContext: true);
        var invalid = valid with { Documents = [valid.Documents![0], valid.Documents[0]] };
        Assert.IsType<BadRequestObjectResult>(
            await duplicate.ChangeCounterpartyAsync(Guid.NewGuid(), invalid, CancellationToken.None));
    }

    private static InternalDocumentsController Controller(CapturingChangeService service, bool includeContext)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["InternalApi:Key"] = "key" }).Build();
        var context = new DefaultHttpContext();
        context.Request.Headers[InternalApiHeaders.ApiKey] = "key";
        context.Request.Headers[InternalApiHeaders.UserId] = Guid.NewGuid().ToString("D");
        if (includeContext)
        {
            context.Request.Headers[InternalApiHeaders.MergeJobId] = Guid.NewGuid().ToString("D");
            context.Request.Headers[InternalApiHeaders.OperationId] = Guid.NewGuid().ToString("D");
        }
        return new InternalDocumentsController(new NoopDiscovery(), service, configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class CapturingChangeService : IMoySkladDocumentChangeService
    {
        public bool HasFailure { get; init; }
        public Guid MergeJobId { get; private set; }
        public Guid OperationId { get; private set; }
        public string? CorrelationId { get; private set; }

        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId,
            string correlationId, MoySkladDocumentChangeCounterpartyRequest request,
            CancellationToken cancellationToken)
        {
            MergeJobId = mergeJobId;
            OperationId = operationId;
            CorrelationId = correlationId;
            var failures = HasFailure
                ? new[] { new MoySkladDocumentChangeFailure(
                    request.Documents![0].DocumentType, request.Documents[0].DocumentId,
                    "MOYSKLAD_3008", "error", 400, false) }
                : [];
            return Task.FromResult(new MoySkladDocumentChangeCounterpartyResponse(
                request.MainCounterpartyId, request.Documents!.Count, 0, 0, failures.Length, [], [], failures));
        }
    }

    private sealed class NoopDiscovery : IMoySkladDocumentDiscoveryService
    {
        public Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
            Guid accountId, Guid requestedByUserId, string correlationId,
            IReadOnlyList<Guid> counterpartyIds, CancellationToken cancellationToken) =>
            Task.FromResult(new MoySkladDocumentDiscoveryResponse([], []));
    }
}
