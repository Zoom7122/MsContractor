using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.CatalogSyncService.Services;
using MsContractor.Contracts.Merge;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.Sync.Tests;

public sealed class MergeProcessorTests
{
    [Fact]
    public async Task ProcessAsync_UpdatesMainArchivesLocallyArchivedDuplicateAndIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        fixture.Duplicates[0].Archived = true;
        await fixture.Db.SaveChangesAsync();
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var main = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Main.Id);
        var duplicate = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Duplicates[0].Id);
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var documents = await fixture.Db.CounterpartyDocuments.OrderBy(item => item.DocumentType).ToListAsync();
        Assert.Equal("Updated Main", main.Name);
        Assert.Equal("updated main", main.NormalizedName);
        Assert.Equal("new@example.test", main.NormalizedEmail);
        Assert.Equal("+79991234567", main.NormalizedPhone);
        Assert.True(duplicate.Archived);
        Assert.Equal(2, documents.Count);
        Assert.Contains(documents, item => item.CounterpartyId == fixture.Main.Id && item.DocumentType == "customerorder");
        Assert.Contains(documents, item => item.CounterpartyId == fixture.Main.Id && item.DocumentType == "demand");
        Assert.Equal(MergeJobStatuses.Completed, job.Status);
        Assert.Equal(MergeOperationStatuses.Completed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.DiscoverDocuments).Status);
        Assert.Single(fixture.Egress.ArchiveCalls);
        Assert.Single(fixture.DocumentChanges.Calls);
        Assert.Equal([fixture.Duplicates[0].Id], fixture.Egress.ArchiveCalls[0]);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Single(fixture.Egress.UpdateCalls);
        Assert.Single(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_UpdateFailureDoesNotChangeMainOrArchiveDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.UpdateException = new MergeEgressException(
            "MOYSKLAD_VALIDATION_FAILED", "MoySklad rejected the counterparty update.", 400);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var main = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Main.Id);
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal("Main", main.Name);
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty).Status);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_PersistsAdditionalContractOnlyForCommissionReports()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var contractId = Guid.NewGuid();
        var commissionInId = Guid.NewGuid();
        var commissionOutId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("demand", Guid.NewGuid(), fixture.Main.Id, Guid.NewGuid()),
            new MoySkladDocumentReference("commissionreportin", commissionInId, fixture.Duplicates[0].Id, contractId),
            new MoySkladDocumentReference("commissionreportout", commissionOutId, fixture.Duplicates[0].Id)
        ];
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var additionalData = await fixture.Db.CounterpartyDocumentAdditionalData
            .OrderBy(item => item.DocumentId)
            .ToListAsync();
        Assert.Equal(2, additionalData.Count);
        Assert.Contains(additionalData, item => item.DocumentId == commissionInId && item.Contract == contractId);
        Assert.Contains(additionalData, item => item.DocumentId == commissionOutId && item.Contract is null);
    }

    [Fact]
    public async Task ProcessAsync_TransfersCommissionReportsWithContractsBeforeOrdinaryDocuments()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 2);
        var contractId = Guid.NewGuid();
        var firstCommission = Guid.NewGuid();
        var secondCommission = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("commissionreportin", firstCommission, fixture.Duplicates[0].Id, contractId),
            new MoySkladDocumentReference("commissionreportout", secondCommission, fixture.Duplicates[1].Id),
            new MoySkladDocumentReference("demand", Guid.NewGuid(), fixture.Duplicates[0].Id)
        ];
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var commissionCall = Assert.Single(fixture.DocumentChanges.AgentAndContractCalls);
        Assert.Equal(new[] { firstCommission, secondCommission }, commissionCall.Select(item => item.DocumentId));
        Assert.Equal(contractId, commissionCall.Single(item => item.DocumentId == firstCommission).Contract);
        Assert.Null(commissionCall.Single(item => item.DocumentId == secondCommission).Contract);
        Assert.Equal(["agent-and-contract", "counterparty"], fixture.DocumentChanges.CallOrder);
        Assert.Single(fixture.DocumentChanges.Calls);
        Assert.Equal("demand", fixture.DocumentChanges.Calls.Single().Single().DocumentType);
    }

    [Fact]
    public async Task ProcessAsync_ChunksCommissionReportsBy950()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        fixture.Documents.Documents = Enumerable.Range(0, 951)
            .Select(_ => new MoySkladDocumentReference(
                "commissionreportout", Guid.NewGuid(), fixture.Duplicates[0].Id))
            .ToArray();
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Equal([950, 1], fixture.DocumentChanges.AgentAndContractCalls.Select(call => call.Count));
        Assert.Empty(fixture.DocumentChanges.Calls);
    }

    [Fact]
    public async Task ProcessAsync_RetriesOnlyCommissionReportsNotYetTransferred()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1, maxAttempts: 2);
        var changedId = Guid.NewGuid();
        var failedId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("commissionreportin", changedId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("commissionreportout", failedId, fixture.Duplicates[0].Id)
        ];
        fixture.DocumentChanges.AgentAndContractResponse = (mainId, documents) =>
            new MoySkladDocumentChangeCounterpartyResponse(
                mainId,
                documents.Count,
                1,
                0,
                1,
                [new MoySkladDocumentChangeItem(documents[0].DocumentType, documents[0].DocumentId)],
                [],
                [new MoySkladDocumentChangeFailure(
                    documents[1].DocumentType, documents[1].DocumentId, "MOYSKLAD_429", "rate limit", 429, true)]);
        var command = await fixture.CreateJobAsync();

        await Assert.ThrowsAsync<MergeRetryableException>(() => fixture.Processor.ProcessAsync(command, CancellationToken.None));
        fixture.DocumentChanges.AgentAndContractResponse = null;

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Equal(2, fixture.DocumentChanges.AgentAndContractCalls.Count);
        Assert.Equal(new[] { changedId, failedId },
            fixture.DocumentChanges.AgentAndContractCalls[0].Select(item => item.DocumentId));
        Assert.Equal([failedId], fixture.DocumentChanges.AgentAndContractCalls[1].Select(item => item.DocumentId));
    }

    [Fact]
    public async Task ProcessAsync_ArchiveFailureContinuesAndProducesPartialCompletion()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 2);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.ArchiveExceptions[fixture.Duplicates[0].Id] = new MergeEgressException(
            "MOYSKLAD_FORBIDDEN", "MoySklad denied access.", 403);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var duplicates = await fixture.Db.Counterparties
            .Where(item => fixture.Duplicates.Select(duplicate => duplicate.Id).Contains(item.Id))
            .OrderBy(item => item.Name)
            .ToListAsync();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.False(duplicates.Single(item => item.Id == fixture.Duplicates[0].Id).Archived);
        Assert.False(duplicates.Single(item => item.Id == fixture.Duplicates[1].Id).Archived);
        Assert.Single(fixture.Egress.ArchiveCalls);
        Assert.Equal(2, fixture.Egress.ArchiveCalls[0].Count);
        Assert.Equal(MergeJobStatuses.PartiallyCompleted, job.Status);
        Assert.Equal(2, job.Operations.Count(item => item.Status == MergeOperationStatuses.Failed));
    }

    [Fact]
    public async Task ProcessAsync_RetryableFailurePersistsAttemptAndDoesNotCompleteJob()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1, maxAttempts: 2);
        var command = await fixture.CreateJobAsync();
        fixture.Egress.UpdateException = new MergeEgressException(
            "MOYSKLAD_UNAVAILABLE", "MoySklad is unavailable.", 503);

        await Assert.ThrowsAsync<MergeRetryableException>(() =>
            fixture.Processor.ProcessAsync(command, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var update = job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty);
        Assert.Equal(MergeJobStatuses.Running, job.Status);
        Assert.Equal(MergeOperationStatuses.Pending, update.Status);
        Assert.Equal(1, update.AttemptCount);
        Assert.Empty(await fixture.Db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ProcessAsync_DocumentDiscoveryFailureStopsBeforeMainUpdateAndArchive()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        fixture.Documents.Exception = new MergeEgressException(
            "MOYSKLAD_RATE_LIMITED", "MoySklad rate limit exceeded. MoySklad error: code=429, error=Too many requests", 429);

        await Assert.ThrowsAsync<MergeRetryableException>(() =>
            fixture.Processor.ProcessAsync(command, CancellationToken.None));

        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var discovery = job.Operations.Single(item => item.OperationType == MergeOperationTypes.DiscoverDocuments);
        Assert.Equal(MergeOperationStatuses.Pending, discovery.Status);
        Assert.Equal("MOYSKLAD_RATE_LIMITED", discovery.ErrorCode);
        Assert.Contains("Too many requests", discovery.ErrorMessage);
        Assert.Empty(fixture.Egress.UpdateCalls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_DocumentChangePartialFailureKeepsSuccessAndStopsBeforeArchive()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 2);
        var command = await fixture.CreateJobAsync();
        fixture.DocumentChanges.Response = (mainId, documents) =>
            new MoySkladDocumentChangeCounterpartyResponse(
                mainId,
                documents.Count,
                1,
                0,
                1,
                [documents[0]],
                [],
                [new MoySkladDocumentChangeFailure(
                    documents[1].DocumentType, documents[1].DocumentId,
                    "MOYSKLAD_3008", "Document is locked.", 400, false)]);

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        var main = await fixture.Db.Counterparties.SingleAsync(item => item.Id == fixture.Main.Id);
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var stored = await fixture.Db.CounterpartyDocuments.Where(item => item.DocumentType == "demand").ToListAsync();
        Assert.Equal("Updated Main", main.Name);
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties).Status);
        Assert.Single(stored, item => item.CounterpartyId == fixture.Main.Id);
        Assert.Single(stored, item => item.CounterpartyId != fixture.Main.Id);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_RetryableDocumentChangeRetriesOnlyDocumentsStillOnDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 2, maxAttempts: 2);
        var command = await fixture.CreateJobAsync();
        fixture.DocumentChanges.Response = (mainId, documents) =>
            new MoySkladDocumentChangeCounterpartyResponse(
                mainId, documents.Count, 1, 0, 1, [documents[0]], [],
                [new MoySkladDocumentChangeFailure(
                    documents[1].DocumentType, documents[1].DocumentId,
                    "MOYSKLAD_RATE_LIMITED", "Rate limit.", 429, true)]);

        await Assert.ThrowsAsync<MergeRetryableException>(() =>
            fixture.Processor.ProcessAsync(command, CancellationToken.None));
        fixture.DocumentChanges.Response = null;
        fixture.Db.ChangeTracker.Clear();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Equal(2, fixture.DocumentChanges.Calls.Count);
        Assert.Equal(2, fixture.DocumentChanges.Calls[0].Count);
        Assert.Single(fixture.DocumentChanges.Calls[1]);
        Assert.Single(fixture.Egress.UpdateCalls);
        Assert.Single(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_AddsMissingDocumentChangeOperationToLegacyActiveJob()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        var missing = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties);
        fixture.Db.MergeOperations.Remove(missing);
        await fixture.Db.SaveChangesAsync();
        var archive = job.Operations.Single(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate);
        archive.Sequence = 2;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        fixture.Db.ChangeTracker.Clear();
        job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(2, job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties).Sequence);
        Assert.Equal(3, job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.ArchiveDuplicate).Sequence);
        Assert.Equal(MergeJobStatuses.Completed, job.Status);
    }

    private sealed class Fixture(
        SqliteConnection connection,
        CatalogSyncDbContext db,
        FakeMergeEgressClient egress,
        FakeDocumentDiscoveryEgressClient documents,
        FakeDocumentChangeEgressClient documentChanges,
        int maxAttempts) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public SqliteConnection Connection { get; } = connection;
        public CatalogSyncDbContext Db { get; } = db;
        public FakeMergeEgressClient Egress { get; } = egress;
        public FakeDocumentDiscoveryEgressClient Documents { get; } = documents;
        public FakeDocumentChangeEgressClient DocumentChanges { get; } = documentChanges;
        public Guid AccountId { get; } = Guid.NewGuid();
        public Counterparty Main { get; private set; } = null!;
        public List<Counterparty> Duplicates { get; } = [];
        public MergeProcessor Processor => new(
            Db, Egress, Documents, DocumentChanges, new MoySkladCounterpartyParser(), new CounterpartyNormalizer(),
            TimeProvider.System,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Merge:MaxOperationAttempts"] = maxAttempts.ToString()
            }).Build(),
            NullLogger<MergeProcessor>.Instance);

        public static async Task<Fixture> CreateAsync(int duplicateCount, int maxAttempts = 5)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var egress = new FakeMergeEgressClient();
            var documents = new FakeDocumentDiscoveryEgressClient();
            var documentChanges = new FakeDocumentChangeEgressClient();
            var fixture = new Fixture(connection, db, egress, documents, documentChanges, maxAttempts);
            var run = NewRun(fixture.AccountId);
            fixture.Main = NewCounterparty(fixture.AccountId, run, "Main");
            db.Add(run);
            db.Add(fixture.Main);
            for (var index = 0; index < duplicateCount; index++)
            {
                var duplicate = NewCounterparty(fixture.AccountId, run, $"Duplicate {index}");
                fixture.Duplicates.Add(duplicate);
                db.Add(duplicate);
            }
            await db.SaveChangesAsync();
            egress.Names[fixture.Main.Id] = "Updated Main";
            foreach (var duplicate in fixture.Duplicates)
                egress.Names[duplicate.Id] = duplicate.Name;
            documents.Documents = [
                new MoySkladDocumentReference("customerorder", Guid.NewGuid(), fixture.Main.Id),
                .. fixture.Duplicates.Select(item => new MoySkladDocumentReference("demand", Guid.NewGuid(), item.Id))
            ];
            return fixture;
        }

        public async Task<MergeRequested> CreateJobAsync()
        {
            var creator = new MergeJobCreator(Db, TimeProvider.System);
            await creator.CreateAsync(
                AccountId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                new CreateMergeJobRequest(
                    Main.Id,
                    Duplicates.Select(item => item.Id).ToArray(),
                    new MergeMainCounterpartyDto(
                        "Updated Main", "new@example.test", "+79991234567", "confirmed")),
                CancellationToken.None);
            var payload = await Db.OutboxMessages.Select(item => item.Payload).SingleAsync();
            Db.ChangeTracker.Clear();
            return JsonSerializer.Deserialize<MergeRequested>(payload, JsonOptions)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class FakeDocumentChangeEgressClient : IDocumentChangeEgressClient
    {
        public List<IReadOnlyList<MoySkladDocumentChangeItem>> Calls { get; } = [];
        public List<IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem>> AgentAndContractCalls { get; } = [];
        public List<string> CallOrder { get; } = [];
        public Func<Guid, IReadOnlyList<MoySkladDocumentChangeItem>, MoySkladDocumentChangeCounterpartyResponse>? Response { get; set; }
        public Func<Guid, IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem>, MoySkladDocumentChangeCounterpartyResponse>? AgentAndContractResponse { get; set; }

        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeCounterpartyAsync(
            Guid accountId, Guid mainCounterpartyId, IReadOnlyList<MoySkladDocumentChangeItem> documents,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(documents.ToArray());
            CallOrder.Add("counterparty");
            return Task.FromResult(Response?.Invoke(mainCounterpartyId, documents) ??
                new MoySkladDocumentChangeCounterpartyResponse(
                    mainCounterpartyId, documents.Count, documents.Count, 0, 0,
                    documents.ToArray(), [], []));
        }

        public Task<MoySkladDocumentChangeCounterpartyResponse> ChangeAgentAndContractAsync(
            Guid accountId, Guid mainCounterpartyId,
            IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            AgentAndContractCalls.Add(documents.ToArray());
            CallOrder.Add("agent-and-contract");
            var changed = documents.Select(item => new MoySkladDocumentChangeItem(
                item.DocumentType, item.DocumentId)).ToArray();
            return Task.FromResult(AgentAndContractResponse?.Invoke(mainCounterpartyId, documents) ??
                new MoySkladDocumentChangeCounterpartyResponse(
                    mainCounterpartyId, documents.Count, changed.Length, 0, 0, changed, [], []));
        }
    }

    private sealed class FakeMergeEgressClient : IMergeEgressClient
    {
        public List<Guid> UpdateCalls { get; } = [];
        public List<IReadOnlyList<Guid>> ArchiveCalls { get; } = [];
        public Dictionary<Guid, string> Names { get; } = [];
        public Dictionary<Guid, Exception> ArchiveExceptions { get; } = [];
        public Exception? UpdateException { get; set; }

        public Task<MergeEgressResponse> UpdateAsync(
            Guid accountId, Guid counterpartyId, MergeMainCounterpartyDto update,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            UpdateCalls.Add(counterpartyId);
            if (UpdateException is not null)
                return Task.FromException<MergeEgressResponse>(UpdateException);
            return Task.FromResult(new MergeEgressResponse(Json(
                counterpartyId, update.Name, update.Email, update.Phone, update.Description, false)));
        }

        public Task<MergeEgressResponse> ArchiveAsync(
            Guid accountId, IReadOnlyList<Guid> counterpartyIds, Guid mergeJobId,
            Guid userId, Guid correlationId, CancellationToken cancellationToken)
        {
            ArchiveCalls.Add(counterpartyIds.ToArray());
            if (counterpartyIds.Select(id => ArchiveExceptions.GetValueOrDefault(id)).FirstOrDefault(exception => exception is not null) is { } exception)
                return Task.FromException<MergeEgressResponse>(exception);
            var json = JsonSerializer.Serialize(counterpartyIds.Select(counterpartyId => new
            {
                id = counterpartyId,
                name = Names[counterpartyId],
                archived = true,
                updated = "2026-08-07 12:00:00.000"
            }));
            return Task.FromResult(new MergeEgressResponse(json));
        }

        private static string Json(
            Guid id, string name, string? email, string? phone, string? description, bool archived) =>
            JsonSerializer.Serialize(new { id, name, email, phone, description, archived, updated = "2026-08-07 12:00:00.000" });
    }

    private sealed class FakeDocumentDiscoveryEgressClient : IDocumentDiscoveryEgressClient
    {
        public IReadOnlyList<MoySkladDocumentReference> Documents { get; set; } = [];
        public Exception? Exception { get; set; }
        public List<IReadOnlyList<Guid>> Calls { get; } = [];

        public Task<MoySkladDocumentDiscoveryResponse> DiscoverAsync(
            Guid accountId, IReadOnlyList<Guid> counterpartyIds, Guid mergeJobId, Guid operationId,
            Guid userId, Guid correlationId, CancellationToken cancellationToken)
        {
            Calls.Add(counterpartyIds.ToArray());
            if (Exception is not null)
                return Task.FromException<MoySkladDocumentDiscoveryResponse>(Exception);
            return Task.FromResult(new MoySkladDocumentDiscoveryResponse(Documents, []));
        }
    }

    private static SyncRun NewRun(Guid accountId) => new()
    {
        Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), AccountId = accountId,
        RequestedByUserId = Guid.NewGuid(), RequestedMode = "full", ExecutionMode = "full",
        Status = "completed", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Counterparty NewCounterparty(Guid accountId, SyncRun run, string name) => new()
    {
        Id = Guid.NewGuid(), AccountId = accountId, Name = name,
        NormalizedName = name.ToLowerInvariant(), RawJson = "{}",
        LastSyncRun = run, LastSyncRunId = run.Id,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}

public sealed class DocumentChangeEgressClientTests
{
    [Fact]
    public async Task ChangeAgentAndContractAsync_SendsDedicatedEndpointAndContractPayload()
    {
        var accountId = Guid.NewGuid();
        var mainId = Guid.NewGuid();
        var document = new MoySkladDocumentChangeAgentAndContractItem(
            "commissionreportin", Guid.NewGuid(), Guid.NewGuid());
        var client = new DocumentChangeEgressClient(
            new HttpClient(new CallbackHandler(async (request, cancellationToken) =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(
                    $"/internal/accounts/{accountId:D}/documents/change-agent-and-contract",
                    request.RequestUri!.AbsolutePath);
                Assert.Equal("key", request.Headers.GetValues(InternalApiHeaders.ApiKey).Single());
                var payload = await request.Content!.ReadFromJsonAsync<MoySkladDocumentChangeAgentAndContractRequest>(cancellationToken);
                Assert.Equal(mainId, payload!.MainCounterpartyId);
                Assert.Equal(document, Assert.Single(payload.Documents!));
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new MoySkladDocumentChangeCounterpartyResponse(
                        mainId, 1, 1, 0, 0,
                        [new MoySkladDocumentChangeItem(document.DocumentType, document.DocumentId)], [], []))
                };
            })) { BaseAddress = new Uri("http://egress/") },
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["InternalApi:Key"] = "key" }).Build());

        var response = await client.ChangeAgentAndContractAsync(
            accountId, mainId, [document], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);

        Assert.Single(response.ChangedDocuments);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
