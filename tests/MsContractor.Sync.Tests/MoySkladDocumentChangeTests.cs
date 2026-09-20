using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways.Documents;
using MsContractor.MoySkladEgressService.RateLimiting;
using MsContractor.MoySkladEgressService.RateLimiting.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Services.Documents;

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

    [Fact]
    public void Parse_AcceptsDocumentsWithRawAdditionalData()
    {
        var options = MoySkladDocumentDiscoveryOptions.Parse(
            "purchasereturn,salesreturn,retailsalesreturn,factureout,facturein");

        Assert.Equal(
            ["purchasereturn", "salesreturn", "retailsalesreturn", "factureout", "facturein"],
            options.DocumentTypes);
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

    [Fact]
    public async Task ChangeCounterpartyAsync_RejectsWrongAgentOnSuccessfulPut()
    {
        var document = Documents("demand", 1)[0];
        var gateway = Gateway((_, _) => Task.FromResult(JsonResponse(Changed(document, Guid.NewGuid()))));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            Guid.NewGuid(), "demand", [document], CancellationToken.None));

        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", exception.Code);
        Assert.Contains("expected_agent_id=", exception.ValidationError);
        Assert.False(exception.Retryable);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_RejectsMalformedSuccessfulPut()
    {
        var document = Documents("demand", 1)[0];
        var gateway = Gateway((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "application/json")
        }));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            Guid.NewGuid(), "demand", [document], CancellationToken.None));

        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", exception.Code);
        Assert.Equal(200, exception.HttpStatus);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_MatchesBulkResponseByDocumentIdInsteadOfOrder()
    {
        var target = Guid.NewGuid();
        var documents = Documents("demand", 3);
        var gateway = Gateway((_, _) => Task.FromResult(JsonResponse(
            documents.Reverse().Select(item => Changed(item, target)).ToArray())));

        var result = await gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            target, "demand", documents, CancellationToken.None);

        Assert.Equal(documents, result.ChangedDocuments);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_ReturnsSpecificMissingBulkDocument()
    {
        var target = Guid.NewGuid();
        var documents = Documents("demand", 3);
        var gateway = Gateway((_, _) => Task.FromResult(JsonResponse(new[]
        {
            Changed(documents[2], target), Changed(documents[0], target)
        })));

        var result = await gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            target, "demand", documents, CancellationToken.None);

        var failure = Assert.Single(result.Failures);
        Assert.Equal(documents[1].DocumentId, failure.DocumentId);
        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", failure.Code);
        Assert.Contains("missing", failure.Message);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_ReturnsSpecificWrongAgentBulkDocument()
    {
        var target = Guid.NewGuid();
        var documents = Documents("demand", 2);
        var gateway = Gateway((_, _) => Task.FromResult(JsonResponse(new[]
        {
            Changed(documents[0], target), Changed(documents[1], Guid.NewGuid())
        })));

        var result = await gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            target, "demand", documents, CancellationToken.None);

        Assert.Equal([documents[0]], result.ChangedDocuments);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(documents[1].DocumentId, failure.DocumentId);
        Assert.Contains("actual_agent_id=", failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"errors\":[{\"code\":3008,\"error\":\"rejected\"}]}", "MOYSKLAD_HTTP_ERROR", false)]
    [InlineData(HttpStatusCode.BadRequest, "not-json", "MOYSKLAD_UNSTRUCTURED_ERROR_RESPONSE", false)]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"errors\":[{\"code\":429,\"error\":\"limit\"}]}", "MOYSKLAD_RATE_LIMITED", true)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"errors\":[{\"code\":1000,\"error\":\"temporary\"}]}", "MOYSKLAD_HTTP_ERROR", true)]
    public async Task ChangeCounterpartyAsync_NormalizesHttpErrors(
        HttpStatusCode status, string body, string code, bool retryable)
    {
        var document = Documents("demand", 1)[0];
        var gateway = Gateway((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            Guid.NewGuid(), "demand", [document], CancellationToken.None));

        Assert.Equal(code, exception.Code);
        Assert.Equal((int)status, exception.HttpStatus);
        Assert.Equal(retryable, exception.Retryable);
        if (body == "not-json") Assert.Equal("not-json", exception.SanitizedResponseBody);
        else Assert.NotNull(exception.MoySkladErrorCode);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_SanitizesSensitiveDiagnosticResponse()
    {
        var document = Documents("demand", 1)[0];
        const string secret = "secret-access-token";
        var gateway = Gateway((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"{secret}\",\"errors\":[{{\"code\":3008,\"error\":\"Authorization=Bearer {secret}\"}}]}}",
                Encoding.UTF8,
                "application/json")
        }));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            Guid.NewGuid(), "demand", [document], CancellationToken.None));

        Assert.DoesNotContain(secret, exception.SanitizedResponseBody);
        Assert.DoesNotContain(secret, exception.MoySkladErrorMessage);
        Assert.Contains("[REDACTED]", exception.SanitizedResponseBody);
    }

    [Fact]
    public async Task ChangeAgentAndContractAsync_PutsAgentAndContractAndValidatesBoth()
    {
        var mainId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var document = new MoySkladDocumentChangeAgentAndContractItem("commissionreportin", Guid.NewGuid(), contractId);
        var gateway = Gateway(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.EndsWith($"/entity/counterparty/{mainId:D}",
                json.RootElement.GetProperty("agent").GetProperty("meta").GetProperty("href").GetString());
            Assert.EndsWith($"/entity/contract/{contractId:D}",
                json.RootElement.GetProperty("contract").GetProperty("meta").GetProperty("href").GetString());
            return JsonResponse(ChangedWithContract(
                new MoySkladDocumentChangeItem(document.DocumentType, document.DocumentId), mainId, contractId));
        });

        var result = await gateway.ChangeAgentAndContractAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation", mainId,
            document.DocumentType, [document], CancellationToken.None);

        Assert.Equal([new MoySkladDocumentChangeItem(document.DocumentType, document.DocumentId)], result.ChangedDocuments);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ChangeAgentAndContractAsync_WithNullContractSendsOnlyAgent()
    {
        var mainId = Guid.NewGuid();
        var document = new MoySkladDocumentChangeAgentAndContractItem("demand", Guid.NewGuid(), null);
        var gateway = Gateway(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("agent", out _));
            Assert.False(json.RootElement.TryGetProperty("contract", out _));
            return JsonResponse(Changed(new MoySkladDocumentChangeItem(document.DocumentType, document.DocumentId), mainId));
        });

        var result = await gateway.ChangeAgentAndContractAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation", mainId,
            document.DocumentType, [document], CancellationToken.None);

        Assert.Single(result.ChangedDocuments);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task ChangeContractAgentsAsync_UsesBatchForSeveralUniqueContracts()
    {
        var mainId = Guid.NewGuid();
        var contracts = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var gateway = Gateway(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/remap/1.2/entity/contract/batch", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.Equal(2, json.RootElement.GetArrayLength());
            return JsonResponse(contracts.Select(id => Changed(new MoySkladDocumentChangeItem("contract", id), mainId)).ToArray());
        });

        var result = await gateway.ChangeContractAgentsAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation", mainId,
            contracts, CancellationToken.None);

        Assert.Equal(2, result.ChangedDocuments.Count);
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

    private static object ChangedWithContract(MoySkladDocumentChangeItem item, Guid mainId, Guid contractId) => new
    {
        id = item.DocumentId,
        meta = new { type = item.DocumentType },
        agent = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{mainId:D}" } },
        contract = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/contract/{contractId:D}" } }
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
        public Task WaitAsync(Guid accountId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ObserveAsync(Guid accountId, MoySkladRateLimitObservation observation, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

public sealed class MoySkladDocumentChangeServiceTests
{
    [Fact]
    public async Task ChangeAgentAndContractAsync_DeduplicatesContractsAndBlocksOnlyTheirDocuments()
    {
        var sharedContract = Guid.NewGuid();
        var failedContract = Guid.NewGuid();
        var first = new MoySkladDocumentChangeAgentAndContractItem("commissionreportin", Guid.NewGuid(), sharedContract);
        var second = new MoySkladDocumentChangeAgentAndContractItem("commissionreportin", Guid.NewGuid(), sharedContract);
        var blocked = new MoySkladDocumentChangeAgentAndContractItem("commissionreportin", Guid.NewGuid(), failedContract);
        var withoutContract = new MoySkladDocumentChangeAgentAndContractItem("commissionreportin", Guid.NewGuid(), null);
        var gateway = new RecordingGateway
        {
            ContractCallback = ids => new MoySkladDocumentChangeChunkResult(
                ids.Where(id => id != failedContract).Select(id => new MoySkladDocumentChangeItem("contract", id)).ToArray(),
                [new MoySkladDocumentChangeFailure("contract", failedContract, "MOYSKLAD_3008", "rejected", 400, false)])
        };
        var service = new MoySkladDocumentAgentAndContractService(gateway,
            new MoySkladDocumentAgentAndContractOptions { DocumentTypes = ["commissionreportin"] },
            NullLogger<MoySkladDocumentAgentAndContractService>.Instance);

        var response = await service.ChangeAgentAndContractAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            new MoySkladDocumentChangeAgentAndContractRequest(Guid.NewGuid(), [first, second, blocked, withoutContract]),
            CancellationToken.None);

        Assert.Equal(new[] { sharedContract, failedContract }.OrderBy(id => id),
            gateway.ContractCalls.Single().OrderBy(id => id));
        Assert.Equal(new[] { first.DocumentId, second.DocumentId, withoutContract.DocumentId },
            gateway.AgentAndContractCalls.Single().Documents.Select(item => item.DocumentId));
        Assert.Equal(3, response.ChangedCount);
        Assert.Equal(blocked.DocumentId, Assert.Single(response.Failures).DocumentId);
    }

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
        public List<Guid[]> ContractCalls { get; } = [];
        public List<AgentAndContractCall> AgentAndContractCalls { get; } = [];
        public Func<MutationCall, MoySkladDocumentChangeChunkResult>? Callback { get; init; }
        public Func<IReadOnlyList<Guid>, MoySkladDocumentChangeChunkResult>? ContractCallback { get; init; }

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

        public Task<MoySkladDocumentChangeChunkResult> ChangeContractAgentsAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            Guid mainCounterpartyId, IReadOnlyList<Guid> contractIds, CancellationToken cancellationToken)
        {
            ContractCalls.Add(contractIds.ToArray());
            return Task.FromResult(ContractCallback?.Invoke(contractIds) ?? new MoySkladDocumentChangeChunkResult(
                contractIds.Select(id => new MoySkladDocumentChangeItem("contract", id)).ToArray(), []));
        }

        public Task<MoySkladDocumentChangeChunkResult> ChangeAgentAndContractAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            Guid mainCounterpartyId, string documentType,
            IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents, CancellationToken cancellationToken)
        {
            var call = new AgentAndContractCall(documentType, documents.ToArray());
            AgentAndContractCalls.Add(call);
            return Task.FromResult(new MoySkladDocumentChangeChunkResult(
                documents.Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId)).ToArray(), []));
        }
    }

    private sealed record MutationCall(string DocumentType, IReadOnlyList<MoySkladDocumentChangeItem> Documents);
    private sealed record AgentAndContractCall(
        string DocumentType, IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> Documents);
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
        return new InternalDocumentsController(
            new NoopDiscovery(), service, new NoopAgentAndContractService(),
            new TestMergeVerificationSnapshotService(), configuration)
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

    private sealed class NoopAgentAndContractService : IMoySkladDocumentAgentAndContractService
    {
        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            MoySkladDocumentChangeAgentAndContractRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
