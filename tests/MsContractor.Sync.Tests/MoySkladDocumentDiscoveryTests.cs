using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Clients;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Controllers;
using MsContractor.MoySkladEgressService.Services;

namespace MsContractor.Sync.Tests;

public sealed class MoySkladDocumentGatewayTests
{
    [Fact]
    public async Task SalesReturnDiscoveryLoadsAllPositionPagesIntoTheSnapshot()
    {
        var id = Guid.NewGuid(); var agent = Guid.NewGuid();
        var limiter = new FakeRateLimiter();
        var token = new FakeTokenClient();
        var calls = new List<string>();
        var gateway = CreateGateway((request, _) =>
        {
            calls.Add(request.RequestUri!.PathAndQuery);
            if (!request.RequestUri.AbsolutePath.Contains("/positions"))
                return Task.FromResult(Response(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
                {
                    meta = new { size = 1, limit = 1000, offset = 0 },
                    rows = new[] { new { id, agent = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{agent}", type = "counterparty" } },
                        positions = new { meta = new { size = 1001 } } } }
                })));
            var offset = request.RequestUri.Query.Contains("offset=1000") ? 1000 : 0;
            return Task.FromResult(Response(HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(new
            {
                meta = new { size = 1001, limit = 1000, offset },
                rows = Enumerable.Range(offset, offset == 0 ? 1000 : 1).Select(index => new { id = Guid.NewGuid(), quantity = 1, price = index }).ToArray()
            })));
        }, token, limiter);
        var result = await gateway.GetPageAsync(Guid.NewGuid(), Guid.NewGuid(), "correlation", "salesreturn", [agent], 1000, 0, default);
        using var raw = System.Text.Json.JsonDocument.Parse(Assert.Single(result.Rows).RawJson!);
        Assert.Equal(1001, raw.RootElement.GetProperty("positions").GetArrayLength());
        Assert.Equal(1000, raw.RootElement.GetProperty("positions")[1000].GetProperty("price").GetInt32());
        Assert.Equal(3, calls.Count);
        Assert.Equal(1, token.Calls);
        Assert.Equal(3, limiter.WaitCalls);
        Assert.Equal(3, limiter.ObserveCalls);
    }

    [Fact]
    public async Task SalesReturnDiscoveryRejectsIncompletePositionsBeforeMergeCanDelete()
    {
        var id = Guid.NewGuid(); var agent = Guid.NewGuid();
        var gateway = CreateGateway((request, _) => Task.FromResult(Response(HttpStatusCode.OK,
            request.RequestUri!.AbsolutePath.Contains("/positions")
            ? "{\"meta\":{\"size\":2,\"limit\":1000,\"offset\":0},\"rows\":[]}"
            : System.Text.Json.JsonSerializer.Serialize(new { meta = new { size = 1, limit = 1000, offset = 0 }, rows = new[]
                { new { id, agent = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{agent}", type = "counterparty" } } } } }))));
        var error = await Assert.ThrowsAsync<EgressException>(() => gateway.GetPageAsync(Guid.NewGuid(), Guid.NewGuid(), "correlation", "salesreturn", [agent], 1000, 0, default));
        Assert.Equal("SALESRETURN_POSITIONS_INCOMPLETE", error.Code);
    }

    [Fact]
    public async Task GetPageAsync_BuildsOneEncodedMultiAgentRequestAndUsesExistingDependencies()
    {
        var counterparties = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        HttpRequestMessage? captured = null;
        var limiter = new FakeRateLimiter();
        var tokenClient = new FakeTokenClient();
        var gateway = CreateGateway(
            (request, _) =>
            {
                captured = request;
                return Task.FromResult(Response(HttpStatusCode.OK, PageJson(0, 1000, 0)));
            },
            tokenClient,
            limiter);

        var page = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            counterparties, 1000, 0, CancellationToken.None);

        Assert.Empty(page.Rows);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("/api/remap/1.2/entity/demand", captured.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(captured.RequestUri.Query);
        var expectedFilter = string.Join(
            ';',
            counterparties.Select(id =>
                $"agent=https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{id:D}"));
        Assert.Contains($"filter={expectedFilter}", query);
        Assert.Contains("limit=1000", query);
        Assert.Contains("offset=0", query);
        Assert.DoesNotContain("%22", captured.RequestUri.OriginalString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("secret-token", captured.Headers.Authorization?.Parameter);
        Assert.Contains(captured.Headers.AcceptEncoding, value => value.Value == "gzip");
        Assert.Equal(1, tokenClient.Calls);
        Assert.Equal(1, limiter.WaitCalls);
        Assert.Equal(1, limiter.ObserveCalls);
    }

    [Fact]
    public async Task GetPageAsync_ParsesOnlyRequiredDocumentFields()
    {
        var counterpartyId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            meta = new { size = 1, limit = 1000, offset = 0, irrelevant = "ignored" },
            rows = new[]
            {
                new
                {
                    id = documentId,
                    name = "ignored",
                    agent = new
                    {
                        meta = new
                        {
                            href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}",
                            type = "counterparty",
                            mediaType = "application/json"
                        }
                    }
                }
            }
        });
        var gateway = CreateGateway((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body)));

        var page = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "invoiceout",
            [counterpartyId], 1000, 0, CancellationToken.None);

        var row = Assert.Single(page.Rows);
        Assert.Equal(documentId, row.DocumentId);
        Assert.Equal("counterparty", row.AgentType);
        Assert.EndsWith(counterpartyId.ToString("D"), row.AgentHref);
        using var raw = System.Text.Json.JsonDocument.Parse(row.RawJson!);
        Assert.Equal("ignored", raw.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, page.Size);
        Assert.Equal(1000, page.Limit);
        Assert.Equal(0, page.Offset);
    }

    [Theory]
    [InlineData("commissionreportin", true)]
    [InlineData("commissionreportout", false)]
    public async Task GetPageAsync_ReadsCommissionContractId(string documentType, bool contractHasId)
    {
        var counterpartyId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        object contract = contractHasId
            ? new { id = contractId }
            : new
            {
                meta = new
                {
                    href = $"https://api.moysklad.ru/api/remap/1.2/entity/contract/{contractId:D}"
                }
            };
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            meta = new { size = 1, limit = 1000, offset = 0 },
            rows = new[]
            {
                new
                {
                    id = documentId,
                    agent = new
                    {
                        meta = new
                        {
                            href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}",
                            type = "counterparty"
                        }
                    },
                    contract
                }
            }
        });
        var gateway = CreateGateway((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));

        var page = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", documentType,
            [counterpartyId], 1000, 0, CancellationToken.None);

        Assert.Equal(contractId, Assert.Single(page.Rows).ContractId);
    }

    [Fact]
    public async Task GetPageAsync_DoesNotReadContractForNonCommissionDocument()
    {
        var counterpartyId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            meta = new { size = 1, limit = 1000, offset = 0 },
            rows = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    agent = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}", type = "counterparty" } },
                    contract = new { id = contractId }
                }
            }
        });
        var gateway = CreateGateway((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));

        var page = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [counterpartyId], 1000, 0, CancellationToken.None);

        Assert.Null(Assert.Single(page.Rows).ContractId);
    }

    [Fact]
    public async Task GetPageAsync_ReturnsNullForInvalidCommissionContract()
    {
        var counterpartyId = Guid.NewGuid();
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            meta = new { size = 1, limit = 1000, offset = 0 },
            rows = new[]
            {
                new
                {
                    id = Guid.NewGuid(),
                    agent = new { meta = new { href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}", type = "counterparty" } },
                    contract = new { meta = new { href = "https://api.moysklad.ru/api/remap/1.2/entity/contract/not-a-guid" } }
                }
            }
        });
        var gateway = CreateGateway((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));

        var page = await gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "commissionreportin",
            [counterpartyId], 1000, 0, CancellationToken.None);

        Assert.Null(Assert.Single(page.Rows).ContractId);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"meta\":{\"size\":0,\"limit\":1000,\"offset\":0}}")]
    [InlineData("{\"meta\":{\"size\":1,\"limit\":1000,\"offset\":0},\"rows\":[{\"id\":\"00000000-0000-0000-0000-000000000001\"}]}")]
    public async Task GetPageAsync_RejectsInvalidJsonOrDto(string body)
    {
        var gateway = CreateGateway((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, body)));

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [Guid.NewGuid()], 1000, 0, CancellationToken.None));

        Assert.Equal(502, exception.StatusCode);
        Assert.Equal("MOYSKLAD_RESPONSE_VALIDATION_FAILED", exception.Code);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400, "MOYSKLAD_HTTP_ERROR")]
    [InlineData(HttpStatusCode.Unauthorized, 401, "MOYSKLAD_HTTP_ERROR")]
    [InlineData(HttpStatusCode.Forbidden, 403, "MOYSKLAD_HTTP_ERROR")]
    [InlineData(HttpStatusCode.TooManyRequests, 429, "MOYSKLAD_RATE_LIMITED")]
    [InlineData(HttpStatusCode.InternalServerError, 503, "MOYSKLAD_HTTP_ERROR")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503, "MOYSKLAD_HTTP_ERROR")]
    public async Task GetPageAsync_MapsHttpErrorsAndStillObservesRateLimit(
        HttpStatusCode upstreamStatus,
        int expectedStatus,
        string expectedCode)
    {
        var limiter = new FakeRateLimiter();
        var gateway = CreateGateway(
            (_, _) => Task.FromResult(Response(
                upstreamStatus,
                """{"errors":[{"code":123,"error":"safe","parameter":"filter"}]}""")),
            limiter: limiter);

        var exception = await Assert.ThrowsAsync<EgressException>(() => gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [Guid.NewGuid()], 1000, 0, CancellationToken.None));

        Assert.Equal(expectedStatus, exception.StatusCode);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(1, limiter.WaitCalls);
        Assert.Equal(1, limiter.ObserveCalls);
    }

    [Fact]
    public async Task GetPageAsync_MapsTimeoutAndTokenFailureWithoutSendingPartialRequest()
    {
        var timeoutGateway = CreateGateway((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException()));
        var timeout = await Assert.ThrowsAsync<EgressException>(() => timeoutGateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [Guid.NewGuid()], 1000, 0, CancellationToken.None));
        Assert.Equal("MOYSKLAD_UNAVAILABLE", timeout.Code);

        var requests = 0;
        var tokenClient = new FakeTokenClient
        {
            Exception = new EgressException(503, "ACCESS_TOKEN_UNAVAILABLE", "Token unavailable.")
        };
        var tokenGateway = CreateGateway(
            (_, _) =>
            {
                requests++;
                return Task.FromResult(Response(HttpStatusCode.OK, PageJson(0, 1000, 0)));
            },
            tokenClient);
        var tokenError = await Assert.ThrowsAsync<EgressException>(() => tokenGateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [Guid.NewGuid()], 1000, 0, CancellationToken.None));
        Assert.Equal("ACCESS_TOKEN_UNAVAILABLE", tokenError.Code);
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task GetPageAsync_DoesNotWriteTokenOrAuthorizationToLogs()
    {
        var logger = new CaptureLogger<MoySkladDocumentGateway>();
        var gateway = CreateGateway(
            (_, _) => Task.FromResult(Response(
                HttpStatusCode.BadRequest,
                """{"errors":[{"code":123,"error":"safe","parameter":"filter"}]}""")),
            logger: logger);

        await Assert.ThrowsAsync<EgressException>(() => gateway.GetPageAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", "demand",
            [Guid.NewGuid()], 1000, 0, CancellationToken.None));

        var log = string.Join('\n', logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("secret-token", log);
        Assert.DoesNotContain("Authorization", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("moysklad_error_code=123", log);
        Assert.Contains("parameter=filter", log);
    }

    [Fact]
    public async Task DiscoveryWithRealGateway_UsesEverySupportedPathOnce()
    {
        var paths = new List<string>();
        var limiter = new FakeRateLimiter();
        var tokenClient = new FakeTokenClient();
        var gateway = CreateGateway(
            (request, _) =>
            {
                paths.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(Response(HttpStatusCode.OK, PageJson(0, 1000, 0)));
            },
            tokenClient,
            limiter);
        var service = new MoySkladDocumentDiscoveryService(
            gateway,
            new MoySkladDocumentDiscoveryOptions { DocumentTypes = SupportedMoySkladDocumentTypes.All },
            new CaptureLogger<MoySkladDocumentDiscoveryService>());

        var result = await service.DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(
            SupportedMoySkladDocumentTypes.All.Select(type => $"/api/remap/1.2/entity/{type}"),
            paths);
        Assert.Equal(SupportedMoySkladDocumentTypes.All.Count, tokenClient.Calls);
        Assert.Equal(SupportedMoySkladDocumentTypes.All.Count, limiter.WaitCalls);
        Assert.Equal(SupportedMoySkladDocumentTypes.All.Count, limiter.ObserveCalls);
        Assert.Empty(result.Documents);
        Assert.Equal(SupportedMoySkladDocumentTypes.All.Count, result.Counts.Count);
    }

    [Fact]
    public async Task DiscoveryWithRealGateway_StopsOnRateLimitOnSecondPage()
    {
        var counterpartyId = Guid.NewGuid();
        var requests = 0;
        var limiter = new FakeRateLimiter();
        var gateway = CreateGateway(
            (_, _) =>
            {
                requests++;
                return Task.FromResult(requests == 1
                    ? Response(HttpStatusCode.OK, PageJsonWithRows(1001, 0, 1000, counterpartyId))
                    : Response(
                        HttpStatusCode.TooManyRequests,
                        """{"errors":[{"code":429,"error":"rate limited"}]}"""));
            },
            limiter: limiter);
        var service = new MoySkladDocumentDiscoveryService(
            gateway,
            new MoySkladDocumentDiscoveryOptions { DocumentTypes = SupportedMoySkladDocumentTypes.All },
            new CaptureLogger<MoySkladDocumentDiscoveryService>());

        var exception = await Assert.ThrowsAsync<EgressException>(() => service.DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));

        Assert.Equal("MOYSKLAD_RATE_LIMITED", exception.Code);
        Assert.Equal(2, requests);
        Assert.Equal(2, limiter.WaitCalls);
        Assert.Equal(2, limiter.ObserveCalls);
    }

    private static MoySkladDocumentGateway CreateGateway(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback,
        FakeTokenClient? tokenClient = null,
        FakeRateLimiter? limiter = null,
        ILogger<MoySkladDocumentGateway>? logger = null)
    {
        var httpClient = new HttpClient(new AsyncStubHandler(callback))
        {
            BaseAddress = new Uri("https://api.moysklad.ru/api/remap/1.2/")
        };
        return new MoySkladDocumentGateway(
            httpClient,
            tokenClient ?? new FakeTokenClient(),
            limiter ?? new FakeRateLimiter(),
            logger ?? new CaptureLogger<MoySkladDocumentGateway>());
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string PageJson(int size, int limit, int offset) =>
        $$"""{"meta":{"size":{{size}},"limit":{{limit}},"offset":{{offset}}},"rows":[]}""";

    private static string PageJsonWithRows(int size, int offset, int count, Guid counterpartyId) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            meta = new { size, limit = 1000, offset },
            rows = Enumerable.Range(offset, count).Select(index => new
            {
                id = new Guid(index + 1, 0, 0, new byte[8]),
                agent = new
                {
                    meta = new
                    {
                        href = $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}",
                        type = "counterparty"
                    }
                }
            })
        });

    private sealed class AsyncStubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }

    private sealed class FakeTokenClient : IVendorTokenClient
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; init; }

        public Task<string> GetAccessTokenAsync(Guid accountId, CancellationToken cancellationToken)
        {
            Calls++;
            return Exception is null
                ? Task.FromResult("secret-token")
                : Task.FromException<string>(Exception);
        }
    }

    private sealed class FakeRateLimiter : IMoySkladRateLimiter
    {
        public int WaitCalls { get; private set; }
        public int ObserveCalls { get; private set; }

        public Task WaitAsync(Guid accountId, Guid? userId, CancellationToken cancellationToken)
        {
            WaitCalls++;
            return Task.CompletedTask;
        }

        public Task ObserveAsync(
            Guid accountId,
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            ObserveCalls++;
            return Task.CompletedTask;
        }
    }
}

public sealed class MoySkladDocumentDiscoveryServiceTests
{
    [Fact]
    public async Task DiscoverAsync_VisitsEverySupportedTypeSequentiallyAndIncludesZeroCounts()
    {
        var gateway = new FakeDocumentGateway(call => Task.FromResult(EmptyPage(call.Offset)));
        var service = Service(gateway);

        var result = await service.DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [Guid.NewGuid()], CancellationToken.None);

        Assert.Empty(result.Documents);
        Assert.Equal(SupportedMoySkladDocumentTypes.All, result.Counts.Select(item => item.DocumentType));
        Assert.All(result.Counts, item => Assert.Equal(0, item.Count));
        Assert.Equal(SupportedMoySkladDocumentTypes.All, gateway.Calls.Select(call => call.DocumentType));
        Assert.All(gateway.Calls, call => Assert.Equal(0, call.Offset));
    }

    [Fact]
    public async Task DiscoverAsync_VisitsOnlyConfiguredDocumentTypes()
    {
        var gateway = new FakeDocumentGateway(call => Task.FromResult(EmptyPage(call.Offset)));
        var service = Service(gateway, "demand");

        var result = await service.DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [Guid.NewGuid()], CancellationToken.None);

        Assert.Equal(["demand"], result.Counts.Select(item => item.DocumentType));
        Assert.Equal(["demand"], gateway.Calls.Select(call => call.DocumentType));
    }

    [Fact]
    public async Task DiscoverAsync_PreservesRawDocumentJson()
    {
        var counterpartyId = Guid.NewGuid();
        const string rawJson = "{\"id\":\"raw-document\",\"customField\":\"preserved\"}";
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "salesreturn"
                ? Page(1, 0, new MoySkladDocumentPageRow(
                    Guid.NewGuid(),
                    $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}",
                    "counterparty",
                    RawJson: rawJson))
                : EmptyPage(call.Offset)));

        var result = await Service(gateway, "salesreturn").DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None);

        Assert.Equal(rawJson, Assert.Single(result.Documents).RawJson);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(999, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2000, 2)]
    [InlineData(2001, 3)]
    [InlineData(2450, 3)]
    public async Task DiscoverAsync_LoadsAllExpectedPages(int size, int expectedPages)
    {
        var counterpartyId = Guid.NewGuid();
        var gateway = PagedGateway(size, counterpartyId);
        var service = Service(gateway);

        var result = await service.DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None);

        var customerOrderCalls = gateway.Calls
            .Where(call => call.DocumentType == "customerorder")
            .ToArray();
        Assert.Equal(expectedPages, customerOrderCalls.Length);
        Assert.Equal(
            Enumerable.Range(0, expectedPages).Select(index => index * 1000),
            customerOrderCalls.Select(call => call.Offset));
        Assert.Equal(size, result.Documents.Count(item => item.DocumentType == "customerorder"));
        Assert.Equal(size, result.Counts.Single(item => item.DocumentType == "customerorder").Count);
        Assert.All(result.Documents, item => Assert.Equal(counterpartyId, item.CounterpartyId));
    }

    [Fact]
    public async Task DiscoverAsync_NormalizesDocumentsForDifferentCounterpartiesAndPreservesOrder()
    {
        var firstCounterparty = Guid.NewGuid();
        var secondCounterparty = Guid.NewGuid();
        var firstDocument = Guid.NewGuid();
        var secondDocument = Guid.NewGuid();
        var secondRowWithoutType = new MoySkladDocumentPageRow(
            secondDocument,
            $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{secondCounterparty:D}",
            null);
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? Page(2, 0,
                    Row(firstDocument, firstCounterparty),
                    secondRowWithoutType)
                : EmptyPage(call.Offset)));

        var result = await Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id",
            [firstCounterparty, secondCounterparty], CancellationToken.None);

        Assert.Equal([firstDocument, secondDocument], result.Documents.Select(item => item.DocumentId));
        Assert.Equal([firstCounterparty, secondCounterparty], result.Documents.Select(item => item.CounterpartyId));
        Assert.All(result.Documents, item => Assert.Equal("customerorder", item.DocumentType));
    }

    [Theory]
    [InlineData("employee", "https://api.moysklad.ru/api/remap/1.2/entity/counterparty/00000000-0000-0000-0000-000000000001")]
    [InlineData("counterparty", "not-a-uri")]
    [InlineData("counterparty", "https://api.moysklad.ru/api/remap/1.2/entity/organization/00000000-0000-0000-0000-000000000001")]
    [InlineData("counterparty", "https://api.moysklad.ru/api/remap/1.2/entity/counterparty/not-a-guid")]
    public async Task DiscoverAsync_RejectsInvalidAgent(string agentType, string agentHref)
    {
        var requestedId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? Page(1, 0, new MoySkladDocumentPageRow(Guid.NewGuid(), agentHref, agentType))
                : EmptyPage(call.Offset)));

        var exception = await Assert.ThrowsAsync<EgressException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [requestedId], CancellationToken.None));

        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsAgentOutsideRequestedSet()
    {
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? Page(1, 0, Row(Guid.NewGuid(), Guid.NewGuid()))
                : EmptyPage(call.Offset)));

        var exception = await Assert.ThrowsAsync<EgressException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [Guid.NewGuid()], CancellationToken.None));

        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsEmptyDocumentId()
    {
        var counterpartyId = Guid.NewGuid();
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? Page(1, 0, Row(Guid.Empty, counterpartyId))
                : EmptyPage(call.Offset)));

        var exception = await Assert.ThrowsAsync<EgressException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));

        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsShortEmptyOrDuplicatePagination()
    {
        var counterpartyId = Guid.NewGuid();
        var duplicateId = Guid.NewGuid();

        var emptyGateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType != "customerorder"
                ? EmptyPage(call.Offset)
                : call.Offset == 0
                    ? PageRows(1001, 0, 1000, counterpartyId)
                    : Page(1001, 1000)));
        var empty = await Assert.ThrowsAsync<EgressException>(() => Service(emptyGateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", empty.Code);

        var duplicateGateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType != "customerorder"
                ? EmptyPage(call.Offset)
                : call.Offset == 0
                    ? Page(1001, 0, Row(duplicateId, counterpartyId)) with
                    {
                        Rows = Enumerable.Range(0, 1000)
                            .Select(index => index == 0
                                ? Row(duplicateId, counterpartyId)
                                : Row(DocumentId(index), counterpartyId))
                            .ToArray()
                    }
                    : Page(1001, 1000, Row(duplicateId, counterpartyId))));
        var duplicate = await Assert.ThrowsAsync<EgressException>(() => Service(duplicateGateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", duplicate.Code);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsCountAndMetadataChanges()
    {
        var counterpartyId = Guid.NewGuid();
        var tooManyGateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? Page(1, 0, Row(Guid.NewGuid(), counterpartyId), Row(Guid.NewGuid(), counterpartyId))
                : EmptyPage(call.Offset)));
        var tooMany = await Assert.ThrowsAsync<EgressException>(() => Service(tooManyGateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", tooMany.Code);

        var changedGateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType != "customerorder"
                ? EmptyPage(call.Offset)
                : call.Offset == 0
                    ? PageRows(1001, 0, 1000, counterpartyId)
                    : Page(1002, 1000, Row(Guid.NewGuid(), counterpartyId))));
        var changed = await Assert.ThrowsAsync<EgressException>(() => Service(changedGateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));
        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", changed.Code);
    }

    [Theory]
    [InlineData(999, 0)]
    [InlineData(1000, 1)]
    public async Task DiscoverAsync_RejectsUnexpectedPageLimitOrOffset(int limit, int offset)
    {
        var gateway = new FakeDocumentGateway(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? new MoySkladDocumentPage(0, limit, offset, [], 200)
                : EmptyPage(call.Offset)));

        var exception = await Assert.ThrowsAsync<EgressException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [Guid.NewGuid()], CancellationToken.None));

        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", exception.Code);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotReturnEarlierPagesWhenLaterPageFails()
    {
        var counterpartyId = Guid.NewGuid();
        var gateway = new FakeDocumentGateway(call =>
        {
            if (call.DocumentType != "customerorder")
                return Task.FromResult(EmptyPage(call.Offset));
            if (call.Offset == 0)
                return Task.FromResult(PageRows(1001, 0, 1000, counterpartyId));
            return Task.FromException<MoySkladDocumentPage>(
                new EgressException(503, "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable."));
        });

        var exception = await Assert.ThrowsAsync<EgressException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None));

        Assert.Equal("MOYSKLAD_UNAVAILABLE", exception.Code);
        Assert.Equal(2, gateway.Calls.Count(call => call.DocumentType == "customerorder"));
    }

    [Fact]
    public async Task DiscoverAsync_StopsOnCancellationBetweenPages()
    {
        using var cancellation = new CancellationTokenSource();
        var counterpartyId = Guid.NewGuid();
        var gateway = new FakeDocumentGateway(call =>
        {
            if (call.DocumentType == "customerorder" && call.Offset == 0)
            {
                cancellation.Cancel();
                return Task.FromResult(PageRows(1001, 0, 1000, counterpartyId));
            }
            return Task.FromResult(EmptyPage(call.Offset));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], cancellation.Token));

        Assert.Single(gateway.Calls);
    }

    [Fact]
    public async Task DiscoverAsync_ContinuesAfterZeroCountType()
    {
        var counterpartyId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var gateway = new FakeDocumentGateway(call => Task.FromResult(call.DocumentType switch
        {
            "customerorder" => EmptyPage(call.Offset),
            "demand" => Page(1, 0, Row(documentId, counterpartyId)),
            _ => EmptyPage(call.Offset)
        }));

        var result = await Service(gateway).DiscoverAsync(
            Guid.NewGuid(), Guid.NewGuid(), "correlation-id", [counterpartyId], CancellationToken.None);

        var document = Assert.Single(result.Documents);
        Assert.Equal("demand", document.DocumentType);
        Assert.Equal(documentId, document.DocumentId);
        Assert.Equal(0, result.Counts.Single(item => item.DocumentType == "customerorder").Count);
        Assert.Equal(1, result.Counts.Single(item => item.DocumentType == "demand").Count);
    }

    private static MoySkladDocumentDiscoveryService Service(
        FakeDocumentGateway gateway,
        params string[] documentTypes) =>
        new(gateway, DiscoveryOptions(documentTypes), new CaptureLogger<MoySkladDocumentDiscoveryService>());

    private static MoySkladDocumentDiscoveryOptions DiscoveryOptions(params string[] documentTypes) =>
        new() { DocumentTypes = documentTypes.Length == 0 ? SupportedMoySkladDocumentTypes.All : documentTypes };

    private static FakeDocumentGateway PagedGateway(int size, Guid counterpartyId) =>
        new(call => Task.FromResult(
            call.DocumentType == "customerorder"
                ? PageRows(
                    size,
                    call.Offset,
                    Math.Max(0, Math.Min(MoySkladDocumentDiscoveryService.PageSize, size - call.Offset)),
                    counterpartyId)
                : EmptyPage(call.Offset)));

    private static MoySkladDocumentPage EmptyPage(int offset) => Page(0, offset);

    private static MoySkladDocumentPage Page(
        int size,
        int offset,
        params MoySkladDocumentPageRow[] rows) =>
        new(size, 1000, offset, rows, 200);

    private static MoySkladDocumentPage PageRows(
        int size,
        int offset,
        int count,
        Guid counterpartyId) =>
        Page(
            size,
            offset,
            Enumerable.Range(offset, count)
                .Select(index => Row(DocumentId(index), counterpartyId))
                .ToArray());

    private static MoySkladDocumentPageRow Row(Guid documentId, Guid counterpartyId) =>
        new(
            documentId,
            $"https://api.moysklad.ru/api/remap/1.2/entity/counterparty/{counterpartyId:D}",
            "counterparty");

    private static Guid DocumentId(int index) =>
        new(index + 1, 0, 0, new byte[8]);

    private sealed class FakeDocumentGateway(
        Func<DocumentPageCall, Task<MoySkladDocumentPage>> callback) : IMoySkladDocumentGateway
    {
        public List<DocumentPageCall> Calls { get; } = [];

        public Task<MoySkladDocumentPage> GetPageAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            string documentType,
            IReadOnlyList<Guid> counterpartyIds,
            int limit,
            int offset,
            CancellationToken cancellationToken)
        {
            var call = new DocumentPageCall(
                accountId, requestedByUserId, correlationId, documentType,
                counterpartyIds, limit, offset, cancellationToken);
            Calls.Add(call);
            return callback(call);
        }

        public Task<MoySkladDocumentChangeChunkResult> ChangeCounterpartyAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId,
            string correlationId, Guid mainCounterpartyId, string documentType,
            IReadOnlyList<MoySkladDocumentChangeItem> documents, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MoySkladDocumentChangeChunkResult> ChangeContractAgentsAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            Guid mainCounterpartyId, IReadOnlyList<Guid> contractIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MoySkladDocumentChangeChunkResult> ChangeAgentAndContractAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            Guid mainCounterpartyId, string documentType,
            IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record DocumentPageCall(
        Guid AccountId,
        Guid UserId,
        string CorrelationId,
        string DocumentType,
        IReadOnlyList<Guid> CounterpartyIds,
        int Limit,
        int Offset,
        CancellationToken CancellationToken);
}

public sealed class InternalDocumentsControllerTests
{
    [Fact]
    public async Task DiscoverAsync_UsesStandardInternalContextAndReturnsCorrelationHeader()
    {
        var accountId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");
        var counterpartyIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var service = new CapturingDiscoveryService();
        var controller = Controller(service, authorized: true, userId, correlationId);

        var result = await controller.DiscoverAsync(
            accountId,
            new MoySkladDocumentDiscoveryRequest(counterpartyIds),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(accountId, service.AccountId);
        Assert.Equal(userId, service.UserId);
        Assert.Equal(correlationId, service.CorrelationId);
        Assert.Equal(counterpartyIds, service.CounterpartyIds);
        Assert.Equal(correlationId, controller.Response.Headers[InternalApiHeaders.CorrelationId]);
    }

    [Fact]
    public async Task DiscoverAsync_GeneratesCorrelationIdWhenHeaderIsMissing()
    {
        var service = new CapturingDiscoveryService();
        var controller = Controller(service, authorized: true, Guid.NewGuid(), correlationId: null);

        var result = await controller.DiscoverAsync(
            Guid.NewGuid(),
            new MoySkladDocumentDiscoveryRequest([Guid.NewGuid()]),
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(Guid.TryParse(service.CorrelationId, out var generated));
        Assert.NotEqual(Guid.Empty, generated);
        Assert.Equal(service.CorrelationId, controller.Response.Headers[InternalApiHeaders.CorrelationId]);
    }

    [Fact]
    public async Task DiscoverAsync_RequiresInternalAuthenticationAndUserContext()
    {
        var unauthorizedService = new CapturingDiscoveryService();
        var unauthorized = Controller(unauthorizedService, authorized: false, Guid.NewGuid(), null);
        var unauthorizedResult = await unauthorized.DiscoverAsync(
            Guid.NewGuid(),
            new MoySkladDocumentDiscoveryRequest([Guid.NewGuid()]),
            CancellationToken.None);
        Assert.IsType<UnauthorizedObjectResult>(unauthorizedResult);
        Assert.Null(unauthorizedService.AccountId);

        var missingUserService = new CapturingDiscoveryService();
        var missingUser = Controller(missingUserService, authorized: true, userId: null, null);
        var missingUserResult = await missingUser.DiscoverAsync(
            Guid.NewGuid(),
            new MoySkladDocumentDiscoveryRequest([Guid.NewGuid()]),
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(missingUserResult);
        Assert.Null(missingUserService.AccountId);
    }

    [Fact]
    public async Task DiscoverAsync_RejectsInvalidAccountAndCounterpartyIds()
    {
        var duplicate = Guid.NewGuid();
        var invalidRequests = new[]
        {
            (Guid.Empty, new MoySkladDocumentDiscoveryRequest([Guid.NewGuid()])),
            (Guid.NewGuid(), new MoySkladDocumentDiscoveryRequest(null)),
            (Guid.NewGuid(), new MoySkladDocumentDiscoveryRequest([])),
            (Guid.NewGuid(), new MoySkladDocumentDiscoveryRequest([Guid.Empty])),
            (Guid.NewGuid(), new MoySkladDocumentDiscoveryRequest([duplicate, duplicate]))
        };

        foreach (var (accountId, request) in invalidRequests)
        {
            var service = new CapturingDiscoveryService();
            var controller = Controller(service, authorized: true, Guid.NewGuid(), null);

            var result = await controller.DiscoverAsync(accountId, request, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Null(service.AccountId);
        }
    }

    [Fact]
    public async Task DiscoverAsync_MapsControlledDiscoveryErrorWithoutResponseBody()
    {
        var service = new CapturingDiscoveryService
        {
            Exception = new EgressException(
                502,
                "MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE",
                "Discovery incomplete.")
        };
        var controller = Controller(service, authorized: true, Guid.NewGuid(), null);

        var result = await controller.DiscoverAsync(
            Guid.NewGuid(),
            new MoySkladDocumentDiscoveryRequest([Guid.NewGuid()]),
            CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result);
        Assert.Equal(502, error.StatusCode);
        var body = Assert.IsType<InternalErrorResponse>(error.Value);
        Assert.Equal("MOYSKLAD_DOCUMENT_DISCOVERY_INCOMPLETE", body.Code);
        Assert.Equal(service.CorrelationId, controller.Response.Headers[InternalApiHeaders.CorrelationId]);
    }

    private static InternalDocumentsController Controller(
        CapturingDiscoveryService service,
        bool authorized,
        Guid? userId,
        string? correlationId)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["InternalApi:Key"] = "test-key"
            })
            .Build();
        var context = new DefaultHttpContext();
        if (authorized)
            context.Request.Headers[InternalApiHeaders.ApiKey] = "test-key";
        if (userId is not null)
            context.Request.Headers[InternalApiHeaders.UserId] = userId.Value.ToString("D");
        if (correlationId is not null)
            context.Request.Headers[InternalApiHeaders.CorrelationId] = correlationId;

        return new InternalDocumentsController(service, new NoopDocumentChangeService(), new NoopAgentAndContractService(), configuration)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class NoopDocumentChangeService : IMoySkladDocumentChangeService
    {
        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId,
            string correlationId, MoySkladDocumentChangeCounterpartyRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new MoySkladDocumentChangeCounterpartyResponse(
                request.MainCounterpartyId, request.Documents?.Count ?? 0, 0, 0, 0, [], [], []));
    }

    private sealed class CapturingDiscoveryService : IMoySkladDocumentDiscoveryService
    {
        public Guid? AccountId { get; private set; }
        public Guid? UserId { get; private set; }
        public string? CorrelationId { get; private set; }
        public IReadOnlyList<Guid>? CounterpartyIds { get; private set; }
        public Exception? Exception { get; init; }

        public Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
            Guid accountId,
            Guid requestedByUserId,
            string correlationId,
            IReadOnlyList<Guid> counterpartyIds,
            CancellationToken cancellationToken)
        {
            AccountId = accountId;
            UserId = requestedByUserId;
            CorrelationId = correlationId;
            CounterpartyIds = counterpartyIds;
            return Exception is null
                ? Task.FromResult(new MoySkladDocumentDiscoveryResponse([], []))
                : Task.FromException<MoySkladDocumentDiscoveryResponse>(Exception);
        }
    }

    private sealed class NoopAgentAndContractService : IMoySkladDocumentAgentAndContractService
    {
        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
            Guid accountId, Guid requestedByUserId, Guid mergeJobId, Guid operationId, string correlationId,
            MoySkladDocumentChangeAgentAndContractRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

file sealed class CaptureLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter) =>
        Entries.Add(new LogEntry(logLevel, formatter(state, exception)));

    public sealed record LogEntry(LogLevel Level, string Message);
}
