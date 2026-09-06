using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MsContractor.Contracts.Internal;
using MsContractor.MoySkladEgressService.Gateways;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services;
using Npgsql;

namespace MsContractor.Sync.Tests;

public sealed class SalesReturnPostgresFactAttribute : FactAttribute
{
    public SalesReturnPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EGRESS_TEST_POSTGRES")))
            Skip = "Set EGRESS_TEST_POSTGRES to an isolated PostgreSQL instance with CREATEDB permission.";
    }
}

public sealed class SalesReturnRecreationTests
{
    internal static readonly Uri BaseUri = new("https://api.moysklad.ru/api/remap/1.2/");
    internal static SalesReturnPayloadBuilder Builder() => new(Options.Create(new EgressOptions { JsonApiBaseUrl = BaseUri }));
    internal static JsonObject Reference(string type, Guid? id = null) => new()
    {
        ["meta"] = new JsonObject { ["href"] = new Uri(BaseUri, $"entity/{type}/{id ?? Guid.NewGuid():D}").AbsoluteUri,
            ["type"] = type, ["mediaType"] = "application/json" }
    };
    internal static RecreateSalesReturnsRequest Request(int count = 1) => new(Guid.NewGuid(), Enumerable.Range(0, count)
        .Select(_ => new RecreateSalesReturnItem(Guid.NewGuid(), Guid.NewGuid(), null, null, new SalesReturnCopyData
        {
            Name = "00001", Moment = "2026-09-06 12:30:00", Applicable = false, Shared = false,
            Description = "Возврат", Code = "", ExternalCode = "original", Organization = Reference("organization"),
            Agent = Reference("counterparty"), AgentAccount = Reference("account"), Contract = Reference("contract"),
            Store = Reference("store"), VatEnabled = false, VatIncluded = false,
            Positions = [new JsonObject { ["id"] = Guid.NewGuid(), ["meta"] = new JsonObject(), ["accountId"] = Guid.NewGuid(),
                ["assortment"] = Reference("product"), ["quantity"] = 2, ["price"] = 0, ["discount"] = 0, ["vat"] = 0, ["vatEnabled"] = false }]
        })).ToArray());
    internal static SalesReturnCallContext Context() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test-correlation");

    [Fact]
    public void Payload_PreservesFalseZeroAndReferencesButRemovesOldIdentityAndAccountContract()
    {
        var request = Request();
        Builder().Validate(request);
        var syncId = Guid.NewGuid();
        var json = JsonNode.Parse(Builder().Build(request.Documents[0], request.MainCounterpartyId, syncId))!;
        Assert.False(json["applicable"]!.GetValue<bool>());
        Assert.False(json["shared"]!.GetValue<bool>());
        Assert.Equal("", json["code"]!.GetValue<string>());
        Assert.Equal("original", json["externalCode"]!.GetValue<string>());
        Assert.Equal(0, json["positions"]![0]!["price"]!.GetValue<int>());
        Assert.Null(json["agentAccount"]);
        Assert.Null(json["contract"]);
        Assert.Null(json["positions"]![0]!["id"]);
        Assert.Null(json["positions"]![0]!["meta"]);
        Assert.NotNull(json["positions"]![0]!["assortment"]!["meta"]);
        Assert.EndsWith(request.MainCounterpartyId.ToString(), json["agent"]!["meta"]!["href"]!.GetValue<string>());
        Assert.Equal(syncId.ToString(), json["syncId"]!.GetValue<string>());
    }

    [Fact]
    public void Payload_ReplacesAccountAndContractWithExplicitNewIds()
    {
        var request = Request();
        var item = request.Documents[0] with { NewAgentAccountId = Guid.NewGuid(), NewContractId = Guid.NewGuid() };
        var json = JsonNode.Parse(Builder().Build(item, request.MainCounterpartyId, Guid.NewGuid()))!;
        Assert.EndsWith($"counterparty/{request.MainCounterpartyId}/accounts/{item.NewAgentAccountId}", json["agentAccount"]!["meta"]!["href"]!.ToString());
        Assert.EndsWith($"contract/{item.NewContractId}", json["contract"]!["meta"]!["href"]!.ToString());
    }

    [Fact]
    public void Validation_RejectsDuplicateIdsMissingPositionsAndForeignUrls()
    {
        var request = Request();
        Assert.Equal(400, Assert.Throws<EgressException>(() => Builder().Validate(request with
            { Documents = [request.Documents[0], request.Documents[0]] })).StatusCode);
        Assert.Throws<EgressException>(() => Builder().Validate(request with
            { Documents = [request.Documents[0] with { Data = new SalesReturnCopyData() }] }));
        request.Documents[0].Data.Store!["meta"]!["href"] = "https://attacker.invalid/entity/store/" + Guid.NewGuid();
        Assert.Throws<EgressException>(() => Builder().Validate(request));
    }

    [Fact]
    public void PayloadCopiesEveryDeclaredFieldAndSupportedPositionData()
    {
        var input = JsonSerializer.SerializeToNode(Request().Documents[0].Data, SalesReturnPayloadBuilder.JsonOptions)!.AsObject();
        foreach (var (name, type) in new[] { ("project", "project"), ("salesChannel", "saleschannel"),
                     ("owner", "employee"), ("group", "group"), ("demand", "demand") })
            input[name] = Reference(type);
        var organizationHref = input["organization"]!["meta"]!["href"]!.ToString();
        input["organizationAccount"] = new JsonObject { ["meta"] = new JsonObject
            { ["href"] = organizationHref + "/accounts/" + Guid.NewGuid(), ["type"] = "account" } };
        input["state"] = new JsonObject { ["meta"] = new JsonObject
            { ["href"] = BaseUri + "entity/salesreturn/metadata/states/" + Guid.NewGuid(), ["type"] = "state" } };
        input["rate"] = new JsonObject { ["currency"] = Reference("currency"), ["value"] = 1.25m };
        input["attributes"] = new JsonArray(new JsonObject { ["meta"] = new JsonObject
            { ["href"] = BaseUri + "entity/salesreturn/metadata/attributes/" + Guid.NewGuid(), ["type"] = "attributemetadata" }, ["value"] = "Доп. данные" });
        var position = input["positions"]![0]!.AsObject();
        position["country"] = Reference("country");
        position["gtd"] = new JsonObject { ["name"] = "10101010/010126/0000001" };
        position["things"] = new JsonArray("serial-1", "serial-2");
        position["cost"] = 100;
        position["pack"] = new JsonObject { ["id"] = Guid.NewGuid(), ["quantity"] = 2 };
        var request = Request();
        var item = request.Documents[0] with { Data = input.Deserialize<SalesReturnCopyData>(SalesReturnPayloadBuilder.JsonOptions)! };
        Builder().Validate(request with { Documents = [item] });
        var output = JsonNode.Parse(Builder().Build(item, request.MainCounterpartyId, Guid.NewGuid()))!.AsObject();
        foreach (var name in input.Select(x => x.Key).Except(["agentAccount", "contract"])) Assert.True(output.ContainsKey(name), name);
        Assert.Equal("Доп. данные", output["attributes"]![0]!["value"]!.ToString());
        Assert.Equal(input["rate"]!.ToJsonString(), output["rate"]!.ToJsonString());
        foreach (var name in new[] { "country", "gtd", "things", "cost", "pack" })
            Assert.Equal(position[name]!.ToJsonString(), output["positions"]![0]![name]!.ToJsonString());
    }

    [SalesReturnPostgresFact]
    public async Task LargeRequestCreatesBatchesOfAtMost1000AfterAllDeletes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request(1001);
        var gateway = new FakeGateway(request);
        var result = await fixture.ExecuteAsync(Context(), request, gateway);
        Assert.Equal(1001, result.Documents.Count(x => x.Status == "Completed"));
        Assert.Equal(1001, gateway.Writes.TakeWhile(x => x == "delete").Count());
        Assert.Equal(["create:1000", "create:1"], gateway.Writes.Skip(1001));
    }

    [SalesReturnPostgresFact]
    public async Task AllDeletesPrecedeBatchCreation_AndCompletedReplayDoesNotWrite()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request(3);
        var context = Context();
        var gateway = new FakeGateway(request);
        gateway.BeforeDelete = async _ =>
        {
            var operation = await fixture.ReadAsync(context);
            Assert.All(operation!.Items, item => Assert.Contains(item.SyncId.ToString(), item.Payload));
        };
        var result = await fixture.ExecuteAsync(context, request, gateway);
        Assert.All(result.Documents, x => Assert.Equal("Completed", x.Status));
        Assert.Equal(["delete", "delete", "delete", "create:3"], gateway.Writes);
        var replay = await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal(result.Documents.Select(x => x.NewDocumentId), replay.Documents.Select(x => x.NewDocumentId));
        Assert.Equal(4, gateway.Writes.Count);
    }

    [SalesReturnPostgresFact]
    public async Task ValidationFailureLeavesOriginalUntouched_AndCreatesOtherDocuments()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request(2);
        var gateway = new FakeGateway(request) { InvalidId = request.Documents[0].OldDocumentId };
        var result = await fixture.ExecuteAsync(Context(), request, gateway);
        Assert.Equal("Failed", result.Documents[0].Status);
        Assert.Equal("Validate", result.Documents[0].Stage);
        Assert.Equal("Completed", result.Documents[1].Status);
        Assert.Contains(request.Documents[0].OldDocumentId, gateway.Existing);
        Assert.Equal(["delete", "create:1"], gateway.Writes);
    }

    [SalesReturnPostgresFact]
    public async Task InitialMissingOriginalDoesNotCreateReplacement()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var gateway = new FakeGateway(request);
        gateway.Existing.Clear();
        var result = await fixture.ExecuteAsync(Context(), request, gateway);
        Assert.Equal("Failed", Assert.Single(result.Documents).Status);
        Assert.Empty(gateway.Writes);
    }

    [SalesReturnPostgresFact]
    public async Task PartialDeleteFailureCreatesOnlyConfirmedDeletedDocuments()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request(2);
        var gateway = new FakeGateway(request) { DeleteFailureId = request.Documents[0].OldDocumentId };
        var result = await fixture.ExecuteAsync(Context(), request, gateway);
        Assert.Equal("Failed", result.Documents[0].Status);
        Assert.Null(result.Documents[0].NewDocumentId);
        Assert.Equal("Completed", result.Documents[1].Status);
        Assert.Single(gateway.Created);
    }

    [SalesReturnPostgresFact]
    public async Task DeleteTimeoutAfterActualDeletionIsReconciledBeforeCreation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var gateway = new FakeGateway(request) { TimeoutAfterDelete = true };
        var result = await fixture.ExecuteAsync(Context(), request, gateway);
        Assert.Equal("Completed", Assert.Single(result.Documents).Status);
        Assert.Equal(["delete", "create:1"], gateway.Writes);
    }

    [SalesReturnPostgresFact]
    public async Task RestartAfterDeletionResumesUsingPersistedIntent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var context = Context();
        var gateway = new FakeGateway(request) { CrashAfterDelete = true };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.ExecuteAsync(context, request, gateway));
        Assert.Equal("Deleting", Assert.Single((await fixture.ReadAsync(context))!.Items).Stage);
        gateway.CrashAfterDelete = false;
        var result = await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal("Completed", Assert.Single(result.Documents).Status);
        Assert.Equal(["delete", "create:1"], gateway.Writes);
    }

    [SalesReturnPostgresFact]
    public async Task LostCreateResponseAndRestartReuseSyncIdWithoutDuplicateOrSecondDelete()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var context = Context();
        var gateway = new FakeGateway(request) { CrashAfterCreate = true };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.ExecuteAsync(context, request, gateway));
        var original = Assert.Single(gateway.Created);
        gateway.CrashAfterCreate = false;
        var result = await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal(original.Value, Assert.Single(result.Documents).NewDocumentId);
        Assert.Single(gateway.Created);
        Assert.Equal(["delete", "create:1", "create:1"], gateway.Writes);
    }

    [SalesReturnPostgresFact]
    public async Task PermanentCreateFailureRetainsPayloadAndStopsAutomaticRetries()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var context = Context();
        var gateway = new FakeGateway(request) { RejectCreate = true };
        var result = await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal("Failed", Assert.Single(result.Documents).Status);
        var operation = (await fixture.ReadAsync(context))!;
        Assert.Null(operation.NextAttemptAt);
        Assert.NotEmpty(operation.Items[0].Payload);
        Assert.Equal("Creating", operation.Items[0].Stage);
        await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal(2, gateway.Writes.Count);
    }

    [SalesReturnPostgresFact]
    public async Task ChangedRequestConflictsAndReorderedObjectPropertiesReplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var context = Context();
        var gateway = new FakeGateway(request);
        await fixture.ExecuteAsync(context, request, gateway);
        var conflict = await Assert.ThrowsAsync<EgressException>(() => fixture.ExecuteAsync(context,
            request with { MainCounterpartyId = Guid.NewGuid() }, gateway));
        Assert.Equal(409, conflict.StatusCode);
        var meta = request.Documents[0].Data.Organization!["meta"]!.AsObject();
        var href = meta["href"]!.DeepClone();
        meta.Remove("href");
        meta["href"] = href;
        await fixture.ExecuteAsync(context, request, gateway);
        Assert.Equal(2, gateway.Writes.Count);
    }

    [SalesReturnPostgresFact]
    public async Task OverlappingOperationConflictsWithoutMutations()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = Request();
        var context = Context();
        var gateway = new FakeGateway(request);
        await fixture.ExecuteAsync(context, request, gateway);
        var error = await Assert.ThrowsAsync<EgressException>(() => fixture.ExecuteAsync(context with { OperationId = Guid.NewGuid() }, request, gateway));
        Assert.Equal("SALESRETURN_ALREADY_CLAIMED", error.Code);
        Assert.Equal(2, gateway.Writes.Count);
    }

    [SalesReturnPostgresFact]
    public async Task TenantIsolationCoversReadsWritesClaimsAndLocks()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = Context();
        var request = Request();
        await fixture.ExecuteAsync(context, request, new FakeGateway(request));
        var other = context with { AccountId = Guid.NewGuid() };
        Assert.Null(await fixture.ReadAsync(other));
        await using var db = fixture.Db();
        var repository = new SalesReturnOperationRepository(db);
        var original = (await fixture.ReadAsync(context))!;
        original.AccountId = other.AccountId;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveAsync(original, default));
        await using var held = await repository.TryLockAccountAsync(context.AccountId, default);
        await using var blocked = await repository.TryLockAccountAsync(context.AccountId, default);
        await using var independent = await repository.TryLockAccountAsync(other.AccountId, default);
        Assert.NotNull(held);
        Assert.Null(blocked);
        Assert.NotNull(independent);
        await independent!.DisposeAsync();
        // Identical document and operation UUIDs in another account must not conflict.
        await fixture.ExecuteAsync(other, request, new FakeGateway(request));
        Assert.NotNull(await fixture.ReadAsync(other));
    }

    [SalesReturnPostgresFact]
    public async Task JournalCreationRollsBackWhenClaimInsertionFails()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var db = fixture.Db();
        var operation = new SalesReturnOperation { AccountId = Guid.NewGuid(), OperationId = Guid.NewGuid(),
            RequestJson = "{}", Items = [new SalesReturnOperationItem { OldDocumentId = Guid.NewGuid(), Payload = "{}" }] };
        // Force a database failure on the second table after the operation row was inserted.
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE egress.salesreturn_claims ADD CONSTRAINT reject_test_claim CHECK (false)");
        await Assert.ThrowsAsync<DbUpdateException>(() => new SalesReturnOperationRepository(db).CreateAsync(operation, default));
        await using var verification = fixture.Db();
        Assert.Empty(await verification.SalesReturnOperations.ToListAsync());
    }

    [SalesReturnPostgresFact]
    public async Task RecoveryResumesDueOperationWithFreshScopedRepository()
    {
        await using var fixture = await Fixture.CreateAsync();
        var context = Context();
        var request = Request();
        var gateway = new FakeGateway(request) { CrashAfterDelete = true };
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.ExecuteAsync(context, request, gateway));
        await using var db = fixture.Db();
        var repository = new SalesReturnOperationRepository(db);
        var operation = (await repository.GetAsync(context.AccountId, context.OperationId, default))!;
        operation.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await repository.SaveAsync(operation, default);
        var key = Assert.Single(await repository.GetDueAsync(DateTimeOffset.UtcNow, default));
        gateway.CrashAfterDelete = false;
        await new SalesReturnRecreationService(repository, gateway, Builder(), TimeProvider.System).ResumeAsync(key, default);
        Assert.Equal("Completed", Assert.Single((await fixture.ReadAsync(context))!.Items).Stage);
    }

    internal sealed class FakeGateway(RecreateSalesReturnsRequest request) : IMoySkladSalesReturnGateway
    {
        public HashSet<Guid> Existing { get; } = request.Documents.Select(x => x.OldDocumentId).ToHashSet();
        public Dictionary<Guid, Guid> Created { get; } = [];
        public List<string> Writes { get; } = [];
        public Guid? InvalidId { get; set; }
        public Guid? DeleteFailureId { get; set; }
        public bool TimeoutAfterDelete { get; set; }
        public bool CrashAfterDelete { get; set; }
        public bool CrashAfterCreate { get; set; }
        public bool RejectCreate { get; set; }
        public Func<Guid, Task>? BeforeDelete { get; set; }
        public Task ValidateAsync(SalesReturnCallContext context, Guid mainId, RecreateSalesReturnItem item, CancellationToken cancellationToken)
        {
            if (!Existing.Contains(item.OldDocumentId)) throw new EgressException(404, "NOT_FOUND", "Missing original.");
            if (InvalidId == item.OldDocumentId) throw new EgressException(422, "WRONG_AGENT", "Wrong agent.");
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken) => Task.FromResult(Existing.Contains(oldId));
        public async Task DeleteAsync(SalesReturnCallContext context, Guid oldId, CancellationToken cancellationToken)
        {
            if (BeforeDelete is not null) await BeforeDelete(oldId);
            Writes.Add("delete");
            if (oldId == DeleteFailureId) throw new EgressException(409, "DELETE_REJECTED", "Cannot delete.");
            Existing.Remove(oldId);
            if (CrashAfterDelete) throw new OperationCanceledException();
            if (TimeoutAfterDelete) throw new EgressException(503, "TIMEOUT", "Timeout.");
        }
        public Task<IReadOnlyList<SalesReturnCreated>> CreateAsync(SalesReturnCallContext context, Guid mainId,
            IReadOnlyList<SalesReturnOperationItem> items, CancellationToken cancellationToken)
        {
            Writes.Add("create:" + items.Count);
            if (RejectCreate) throw new EgressException(400, "CREATE_REJECTED", "Cannot create.");
            foreach (var item in items) Created.TryAdd(item.SyncId, Guid.NewGuid());
            if (CrashAfterCreate) throw new OperationCanceledException();
            return Task.FromResult<IReadOnlyList<SalesReturnCreated>>(items.Reverse().Select(item => new SalesReturnCreated(item.SyncId, Created[item.SyncId])).ToArray());
        }
    }

    internal sealed class Fixture(string connectionString, string adminConnection, string database) : IAsyncDisposable
    {
        public EgressDbContext Db() => new(new DbContextOptionsBuilder<EgressDbContext>().UseNpgsql(connectionString,
            pg => pg.MigrationsHistoryTable("__EFMigrationsHistory", "egress")).Options);
        public static async Task<Fixture> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("EGRESS_TEST_POSTGRES")!;
            var database = "egress_test_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {database}", connection);
            await command.ExecuteNonQueryAsync();
            var fixture = new Fixture(new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false }.ConnectionString, admin, database);
            await using var db = fixture.Db();
            await db.Database.MigrateAsync();
            return fixture;
        }
        public async Task<RecreateSalesReturnsResponse> ExecuteAsync(SalesReturnCallContext context, RecreateSalesReturnsRequest request, FakeGateway gateway)
        {
            await using var db = Db();
            return await new SalesReturnRecreationService(new SalesReturnOperationRepository(db), gateway, Builder(), TimeProvider.System)
                .ExecuteAsync(context, request, default);
        }
        public async Task<SalesReturnOperation?> ReadAsync(SalesReturnCallContext context)
        {
            await using var db = Db();
            return await new SalesReturnOperationRepository(db).GetAsync(context.AccountId, context.OperationId, default);
        }
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
