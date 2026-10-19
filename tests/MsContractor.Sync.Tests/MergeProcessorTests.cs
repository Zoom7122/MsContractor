using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.DuplicatesMergeService.Clients;
using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.CatalogSyncService.Services;
using MsContractor.CatalogSyncService.Repositories;
using MsContractor.Contracts.Merge;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Services;
using MsContractor.DuplicatesMergeService.Services.Merge;
using MsContractor.DuplicatesMergeService.Services.Merge.Counterparties;
using MsContractor.DuplicatesMergeService.Services.Merge.Documents;
using MsContractor.DuplicatesMergeService.Models.Options;

namespace MsContractor.Sync.Tests;

public sealed class MergeProcessorTests
{
    private static MergeEgressHttpResponse SalesReturnResult(
        IReadOnlyList<Guid> transferredDocumentIds,
        IReadOnlyList<SalesReturnTransferredDocumentResponse> transferredDocuments,
        IReadOnlyList<Guid>? skippedDocumentIds = null,
        IReadOnlyList<SalesReturnSkippedDocumentResponse>? skippedDocuments = null,
        IReadOnlyList<SalesReturnCreatedWithErrorResponse>? createdWithErrors = null,
        IReadOnlyList<SalesReturnUnCreatedButDeletedResponse>? unCreatedButDeleted = null)
    {
        var response = new SalesReturnRecreationResponse(
            transferredDocumentIds,
            transferredDocuments,
            skippedDocumentIds ?? [],
            skippedDocuments ?? [],
            createdWithErrors ?? [],
            unCreatedButDeleted ?? []);
        return EgressResponse(response);
    }

    private static MergeEgressHttpResponse EgressResponse<T>(T response) => new(
        (int)HttpStatusCode.OK,
        JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public async Task ProcessAsync_RejectsCommandThatDoesNotMatchDurableJob()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var command = await fixture.CreateJobAsync();

        await Assert.ThrowsAsync<MergeCommandRejectedException>(() =>
            fixture.Processor.ProcessAsync(command with { CorrelationId = Guid.NewGuid() }, CancellationToken.None));

        Assert.Empty(fixture.Egress.UpdateCalls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task ProcessAsync_FailsJobAndReleasesRemainingLocksWhenOwnershipIsLost()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var command = await fixture.CreateJobAsync();
        await fixture.Db.MergeCounterpartyLocks
            .Where(item => item.CounterpartyId == fixture.Duplicates[0].Id)
            .ExecuteDeleteAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Empty(fixture.Egress.UpdateCalls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(MergeJobStatuses.Failed, (await fixture.Db.MergeJobs.SingleAsync()).Status);
        Assert.Contains(await fixture.Db.MergeOperations.ToListAsync(), item =>
            item.ErrorCode == "MERGE_COUNTERPARTY_LOCK_LOST" && item.Status == MergeOperationStatuses.Failed);
        Assert.Empty(await fixture.Db.MergeCounterpartyLocks.ToListAsync());
        Assert.Single(await fixture.Db.InboxMessages.ToListAsync());
    }

    [Fact]
    public async Task ProcessAsync_TerminalJobOnlyRecordsInboxMessage()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var command = await fixture.CreateJobAsync();
        var job = await fixture.Db.MergeJobs.SingleAsync();
        job.Status = MergeJobStatuses.Completed;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Single(await fixture.Db.InboxMessages.ToListAsync());
        Assert.Empty(await fixture.Db.MergeCounterpartyLocks.ToListAsync());
        Assert.Empty(fixture.Documents.Calls);
        Assert.Empty(fixture.Egress.UpdateCalls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

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
        Assert.Empty(await fixture.Db.MergeCounterpartyLocks.ToListAsync());
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
    public async Task ProcessAsync_RecreatesSalesReturnsBeforeArchivingDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var salesReturnId = Guid.NewGuid();
        var recreatedId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("salesreturn", salesReturnId, fixture.Duplicates[0].Id)
        ];
        fixture.SalesReturns.Response = (_, _) => SalesReturnResult(
            [salesReturnId],
            [new SalesReturnTransferredDocumentResponse(salesReturnId, recreatedId)]);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Empty(fixture.DocumentChanges.Calls);
        var call = Assert.Single(fixture.SalesReturns.Calls);
        Assert.Equal(fixture.Main.Id, call.MainAgentId);
        Assert.Equal("salesreturn", call.DocumentType);
        Assert.Equal([salesReturnId], call.DocumentIds);
        Assert.Single(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var document = await fixture.Db.CounterpartyDocuments.SingleAsync();
        Assert.Equal(recreatedId, document.DocumentId);
        Assert.Equal(fixture.Main.Id, document.CounterpartyId);
        var audit = await fixture.Db.DocumentsInOperation.SingleAsync();
        Assert.Equal(command.MergeJobId, audit.MergeJobId);
        Assert.Equal(fixture.AccountId, audit.AccountId);
        Assert.Equal(DocumentsInOperationTypes.RecreateSalesReturn, audit.OperationType);
        using var responseArray = JsonDocument.Parse(audit.RawJson);
        Assert.Equal(200, responseArray.RootElement[0].GetProperty("statusCode").GetInt32());
        var storedResponse = JsonSerializer.Deserialize<SalesReturnRecreationResponse>(
            responseArray.RootElement[0].GetProperty("body").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(storedResponse);
        Assert.Equal(recreatedId, Assert.Single(storedResponse!.TransferredDocuments!).NewDocumentId);
    }

    [Fact]
    public async Task ProcessAsync_SkippedSalesReturnDoesNotStopMerge()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var salesReturnId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("salesreturn", salesReturnId, fixture.Duplicates[0].Id)];
        fixture.SalesReturns.Response = (_, _) => SalesReturnResult(
            [],
            [],
            [salesReturnId],
            [new SalesReturnSkippedDocumentResponse(
                salesReturnId, "Skipped", "SALESRETURN_DOCUMENT_SKIPPED", "The source remains available.")]);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Single(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(MergeJobStatuses.Completed, job.Status);
        Assert.Equal(MergeOperationStatuses.Completed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.RecreateSalesReturns).Status);
        var row = await fixture.Db.CounterpartyDocuments.SingleAsync(item => item.DocumentId == salesReturnId);
        Assert.Equal(fixture.Duplicates[0].Id, row.CounterpartyId);
    }

    [Fact]
    public async Task ProcessAsync_GroupsDocumentChangeAndRecreationRepliesByEndpoint()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var demandId = Guid.NewGuid();
        var salesReturnId = Guid.NewGuid();
        var commissionReportId = Guid.NewGuid();
        var factureInId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("demand", demandId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("commissionreportin", commissionReportId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("salesreturn", salesReturnId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("facturein", factureInId, fixture.Duplicates[0].Id)
        ];
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var rows = await fixture.Db.DocumentsInOperation.OrderBy(item => item.OperationType).ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(
            [
                DocumentsInOperationTypes.ChangeAgentAndContract,
                DocumentsInOperationTypes.ChangeCounterparty,
                DocumentsInOperationTypes.RecreateFactureIn,
                DocumentsInOperationTypes.RecreateSalesReturn
            ],
            rows.Select(item => item.OperationType).ToArray());
        Assert.All(rows, row =>
        {
            using var json = JsonDocument.Parse(row.RawJson);
            Assert.Equal(1, json.RootElement.GetArrayLength());
            Assert.Equal(200, json.RootElement[0].GetProperty("statusCode").GetInt32());
        });
    }

    [Fact]
    public async Task ProcessAsync_PersistsStandaloneFactureOutRecreationResponse()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var factureOutId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("factureout", factureOutId, fixture.Duplicates[0].Id)];
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var audit = await fixture.Db.DocumentsInOperation.SingleAsync();
        Assert.Equal(DocumentsInOperationTypes.RecreateFactureOut, audit.OperationType);
        using var response = JsonDocument.Parse(audit.RawJson);
        Assert.Equal(200, response.RootElement[0].GetProperty("statusCode").GetInt32());
        var parsed = JsonSerializer.Deserialize<FactureOutRecreationResponse>(
            response.RootElement[0].GetProperty("body").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        Assert.Contains(factureOutId, parsed!.TransferredDocumentIds);
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("created-with-errors")]
    [InlineData("uncreated-after-delete")]
    public async Task ProcessAsync_OnlyCreatedWithErrorsOrUnCreatedButDeletedFactureOutOutcomesStopMerge(string outcome)
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var factureOutId = Guid.NewGuid();
        var newDocumentId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("factureout", factureOutId, fixture.Duplicates[0].Id)];
        fixture.FactureOuts.Response = _ => outcome switch
        {
            "created-with-errors" => new FactureOutRecreationResponse(
                [], [], [], [new(factureOutId, newDocumentId, "CreatedWithError", "FACTUREOUT_POST_CREATE_FAILED", "Post-create step failed.")], []),
            "uncreated-after-delete" => new FactureOutRecreationResponse([], [], [], [], [])
            {
                UnCreatedButDeleted = [new(factureOutId, "Failed", "FACTUREOUT_CREATE_FAILED", "The replacement was not created.")]
            },
            _ => new FactureOutRecreationResponse(
                [],
                [factureOutId],
                [new(factureOutId, "Skipped", "FACTUREOUT_NOT_READY", "The source remains available.")],
                [],
                [])
        };
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var blocksMerge = outcome != "skipped";
        Assert.Equal(blocksMerge ? 0 : 1, fixture.Egress.ArchiveCalls.Count);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(blocksMerge ? MergeJobStatuses.Failed : MergeJobStatuses.Completed, job.Status);
        Assert.Equal(blocksMerge ? MergeOperationStatuses.Failed : MergeOperationStatuses.Completed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.RecreateFactureOuts).Status);
        var snapshot = await fixture.Db.CounterpartyDocuments.FirstOrDefaultAsync(item => item.DocumentType == "factureout");
        if (outcome == "uncreated-after-delete")
            Assert.Null(snapshot);
        else if (outcome == "created-with-errors")
        {
            Assert.Equal(newDocumentId, snapshot!.DocumentId);
            Assert.Equal(fixture.Main.Id, snapshot.CounterpartyId);
        }
        else
        {
            Assert.Equal(factureOutId, snapshot!.DocumentId);
            Assert.Equal(fixture.Duplicates[0].Id, snapshot.CounterpartyId);
        }
    }

    [Fact]
    public async Task ProcessAsync_PersistsErrorResponseBeforeMappingItToMergeFailure()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1, maxAttempts: 1);
        const string errorBody = "{\"code\":\"EGRESS_BUSY\",\"message\":\"Try again.\"}";
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("demand", Guid.NewGuid(), fixture.Duplicates[0].Id)];
        fixture.DocumentChanges.CounterpartyRawResponse = new MergeEgressHttpResponse(503, errorBody);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var audit = await fixture.Db.DocumentsInOperation.SingleAsync();
        Assert.Equal(DocumentsInOperationTypes.ChangeCounterparty, audit.OperationType);
        using var responseArray = JsonDocument.Parse(audit.RawJson);
        var response = responseArray.RootElement[0];
        Assert.Equal(503, response.GetProperty("statusCode").GetInt32());
        Assert.Equal("EGRESS_BUSY", response.GetProperty("body").GetProperty("code").GetString());
        var job = await fixture.Db.MergeJobs.SingleAsync(item => item.Id == command.MergeJobId);
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Empty(fixture.Egress.ArchiveCalls);
    }

    [Fact]
    public async Task DocumentsInOperationRepository_AppendsPerEndpointAndMergeJob()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        var repository = new DocumentsInOperationRepository(fixture.Db);
        await repository.AppendEgressResponseAsync(
            fixture.AccountId, command.MergeJobId, DocumentsInOperationTypes.ChangeCounterparty,
            200, "{\"batch\":1}", CancellationToken.None);
        await repository.AppendEgressResponseAsync(
            fixture.AccountId, command.MergeJobId, DocumentsInOperationTypes.ChangeCounterparty,
            503, "{\"code\":\"EGRESS_BUSY\"}", CancellationToken.None);
        await repository.AppendEgressResponseAsync(
            fixture.AccountId, command.MergeJobId, DocumentsInOperationTypes.RecreateSalesReturn,
            200, "{\"recreated\":true}", CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var secondJob = new MergeJob
        {
            Id = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            MainCounterpartyId = fixture.Main.Id,
            RequestedByUserId = Guid.NewGuid(),
            Status = MergeJobStatuses.Pending,
            PayloadVersion = 1,
            Payload = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
        fixture.Db.MergeJobs.Add(secondJob);
        await fixture.Db.SaveChangesAsync();
        await repository.AppendEgressResponseAsync(
            fixture.AccountId, secondJob.Id, DocumentsInOperationTypes.ChangeCounterparty,
            200, "{\"batch\":1}", CancellationToken.None);

        var rows = await fixture.Db.DocumentsInOperation.ToListAsync();
        Assert.Equal(3, rows.Count);
        var firstJobChange = Assert.Single(rows, item =>
            item.MergeJobId == command.MergeJobId &&
            item.OperationType == DocumentsInOperationTypes.ChangeCounterparty);
        using var grouped = JsonDocument.Parse(firstJobChange.RawJson);
        Assert.Equal(2, grouped.RootElement.GetArrayLength());
        Assert.Equal(200, grouped.RootElement[0].GetProperty("statusCode").GetInt32());
        Assert.Equal(503, grouped.RootElement[1].GetProperty("statusCode").GetInt32());
        Assert.Single(rows, item => item.MergeJobId == secondJob.Id);
    }

    [Fact]
    public async Task DocumentsInOperationStateRepository_ReturnsOnlyRowsForTenantAndMergeJob()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var command = await fixture.CreateJobAsync();
        const string rawJson = "[{\"statusCode\":207,\"body\":{\"skippedDocuments\":[]}}]";
        fixture.Db.DocumentsInOperation.Add(new DocumentsInOperation
        {
            Id = Guid.NewGuid(),
            AccountId = fixture.AccountId,
            MergeJobId = command.MergeJobId,
            OperationType = DocumentsInOperationTypes.ChangeCounterparty,
            RawJson = rawJson,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();

        var repository = new DocumentsInOperationStateRepository(fixture.Db);
        var rows = await repository.GetForMergeJobAsync(
            fixture.AccountId,
            command.MergeJobId,
            CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(DocumentsInOperationTypes.ChangeCounterparty, row.OperationType);
        Assert.Equal(rawJson, row.RawJson);
        Assert.Empty(await repository.GetForMergeJobAsync(
            fixture.AccountId,
            Guid.NewGuid(),
            CancellationToken.None));

        await using var otherTenantDb = new CatalogSyncDbContext(
            new DbContextOptionsBuilder<CatalogSyncDbContext>()
                .UseSqlite(fixture.Connection)
                .Options);
        var otherTenantRepository = new DocumentsInOperationStateRepository(otherTenantDb);
        Assert.Empty(await otherTenantRepository.GetForMergeJobAsync(
            Guid.NewGuid(),
            command.MergeJobId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ProcessAsync_StoresAndAppliesMixedSalesReturnOutcomesBeforeFailingTheOperation()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var transferredId = Guid.NewGuid();
        var createdWithErrorId = Guid.NewGuid();
        var unCreatedButDeletedId = Guid.NewGuid();
        var skippedId = Guid.NewGuid();
        var transferredReplacementId = Guid.NewGuid();
        var errorReplacementId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("salesreturn", transferredId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("salesreturn", createdWithErrorId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("salesreturn", unCreatedButDeletedId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("salesreturn", skippedId, fixture.Duplicates[0].Id)
        ];
        fixture.SalesReturns.Response = (_, _) => SalesReturnResult(
            [transferredId],
            [new SalesReturnTransferredDocumentResponse(transferredId, transferredReplacementId)],
            [skippedId],
            [new SalesReturnSkippedDocumentResponse(skippedId, "Skipped", "SALESRETURN_FACTURE_OUT_PRESENT", "Has factureOut.")],
            [new SalesReturnCreatedWithErrorResponse(
                createdWithErrorId, errorReplacementId, "CreatedWithError", "SALESRETURN_RELATIONS_REATTACH_FAILED", "Reattach failed.")],
            [new SalesReturnUnCreatedButDeletedResponse(
                unCreatedButDeletedId, "Failed", "SALESRETURN_NEW_CREATE_FAILED", "Create failed.")]);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Empty(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.RecreateSalesReturns).Status);
        var audit = await fixture.Db.DocumentsInOperation.SingleAsync();
        Assert.Equal(DocumentsInOperationTypes.RecreateSalesReturn, audit.OperationType);
        using var responseArray = JsonDocument.Parse(audit.RawJson);
        var savedResponse = JsonSerializer.Deserialize<SalesReturnRecreationResponse>(
            responseArray.RootElement[0].GetProperty("body").GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(savedResponse);
        Assert.Equal([skippedId], savedResponse!.SkippedDocumentIds);
        var documents = await fixture.Db.CounterpartyDocuments.OrderBy(item => item.DocumentId).ToListAsync();
        Assert.Equal(3, documents.Count);
        Assert.Contains(documents, item => item.CounterpartyId == fixture.Main.Id && item.DocumentId == transferredReplacementId);
        Assert.Contains(documents, item => item.CounterpartyId == fixture.Main.Id && item.DocumentId == errorReplacementId);
        Assert.Contains(documents, item => item.CounterpartyId == fixture.Duplicates[0].Id && item.DocumentId == skippedId);
        Assert.DoesNotContain(documents, item => item.DocumentId == unCreatedButDeletedId);
    }

    [Fact]
    public async Task ProcessAsync_AppliesReturnAndInvoiceIdsAndDoesNotRecreateLinkedInvoiceAgain()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var sourceId = Guid.NewGuid(); var invoiceId = Guid.NewGuid(); var otherInvoiceId = Guid.NewGuid();
        var newReturnId = Guid.NewGuid(); var newInvoiceId = Guid.NewGuid();
        var foreignInvoiceId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new("purchasereturn", sourceId, fixture.Duplicates[0].Id),
         new("factureout", invoiceId, fixture.Duplicates[0].Id),
         new("factureout", otherInvoiceId, fixture.Duplicates[0].Id)];
        fixture.PurchaseReturns.Response = ids => new PurchaseReturnRecreationResponse(ids, [])
        {
            TransferredDocuments = [new(sourceId, newReturnId)],
            RecreatedPurchaseReturns = [new(sourceId, newReturnId)],
            RecreatedFactureOuts = [new(sourceId, invoiceId, newInvoiceId), new(sourceId, foreignInvoiceId, Guid.NewGuid())]
        };
        var otherAccountId = Guid.NewGuid();
        var otherRun = NewRun(otherAccountId);
        var otherCounterparty = NewCounterparty(otherAccountId, otherRun, "Other account");
        fixture.Db.Add(otherRun); fixture.Db.Add(otherCounterparty);
        fixture.Db.CounterpartyDocuments.Add(new CounterpartyDocument
        {
            AccountId = otherAccountId, CounterpartyId = otherCounterparty.Id,
            DocumentId = foreignInvoiceId, DocumentType = "factureout", UpdatedAt = DateTimeOffset.UtcNow
        });
        await fixture.Db.SaveChangesAsync();
        var command = await fixture.CreateJobAsync();
        await fixture.Processor.ProcessAsync(command, CancellationToken.None);
        Assert.Equal([otherInvoiceId], Assert.Single(fixture.FactureOuts.Calls).DocumentIds);
        Assert.Single(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var rows = await fixture.Db.CounterpartyDocuments.Where(row => row.AccountId == fixture.AccountId).ToListAsync();
        Assert.Contains(rows, row => row.DocumentId == newReturnId && row.CounterpartyId == fixture.Main.Id);
        Assert.Contains(rows, row => row.DocumentId == newInvoiceId && row.CounterpartyId == fixture.Main.Id);
        var auditTypes = await fixture.Db.DocumentsInOperation
            .OrderBy(item => item.OperationType)
            .Select(item => item.OperationType)
            .ToListAsync();
        Assert.Equal(
            [DocumentsInOperationTypes.RecreateFactureOut, DocumentsInOperationTypes.RecreatePurchaseReturn],
            auditTypes);
        var other = await fixture.Db.CounterpartyDocuments.SingleAsync(row => row.AccountId == otherAccountId);
        Assert.Equal(foreignInvoiceId, other.DocumentId);
        Assert.Equal(otherCounterparty.Id, other.CounterpartyId);
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("created-with-errors")]
    [InlineData("uncreated-after-delete")]
    public async Task ProcessAsync_OnlyCreatedWithErrorsOrUnCreatedButDeletedStopBeforeInvoiceStageAndArchive(string outcome)
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var sourceId = Guid.NewGuid(); var invoiceId = Guid.NewGuid(); var newId = Guid.NewGuid();
        fixture.Documents.Documents = [new("purchasereturn", sourceId, fixture.Duplicates[0].Id),
            new("factureout", invoiceId, fixture.Duplicates[0].Id)];
        fixture.PurchaseReturns.Response = _ => outcome switch
        {
            "created-with-errors" => new PurchaseReturnRecreationResponse(
                [], [], [], [new(sourceId, newId, "CreatedWithError", "INVOICE_FAILED", "Invoice failed.")])
            {
                RecreatedPurchaseReturns = [new(sourceId, newId)]
            },
            "uncreated-after-delete" => new PurchaseReturnRecreationResponse([], [])
            {
                UnCreatedButDeleted = [new(sourceId, "Failed", "CREATE_FAILED", "The replacement was not created.")]
            },
            _ => new PurchaseReturnRecreationResponse(
                [], [sourceId], [new(sourceId, "Skipped", "SHARED_INVOICE", "Shared invoice.")])
        };
        var command = await fixture.CreateJobAsync();
        await fixture.Processor.ProcessAsync(command, CancellationToken.None);
        var blocksMerge = outcome != "skipped";
        Assert.Equal(blocksMerge ? 0 : 1, fixture.FactureOuts.Calls.Count);
        Assert.Equal(blocksMerge ? 0 : 1, fixture.Egress.ArchiveCalls.Count);
        fixture.Db.ChangeTracker.Clear();
        var operation = await fixture.Db.MergeOperations.SingleAsync(item => item.OperationType == MergeOperationTypes.RecreatePurchaseReturns);
        Assert.Equal(blocksMerge ? MergeOperationStatuses.Failed : MergeOperationStatuses.Completed, operation.Status);
        var returned = await fixture.Db.CounterpartyDocuments.FirstOrDefaultAsync(row =>
            row.AccountId == fixture.AccountId && row.DocumentType == "purchasereturn");
        if (outcome == "uncreated-after-delete")
            Assert.Null(returned);
        else
            Assert.Equal(outcome == "created-with-errors" ? newId : sourceId, returned!.DocumentId);
    }

    [Theory]
    [InlineData("missing-map")]
    [InlineData("duplicate")]
    [InlineData("foreign-parent")]
    [InlineData("transport")]
    public async Task ProcessAsync_UnknownReturnResultIsTerminalWithoutAutomaticRetry(string kind)
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var sourceId = Guid.NewGuid(); var newId = Guid.NewGuid();
        fixture.Documents.Documents = [new("purchasereturn", sourceId, fixture.Duplicates[0].Id)];
        fixture.PurchaseReturns.Response = _ => new PurchaseReturnRecreationResponse([sourceId], [])
        {
            TransferredDocuments = [new(sourceId, newId)],
            RecreatedPurchaseReturns = kind == "missing-map" ? [] : kind == "duplicate"
                ? [new(sourceId, newId), new(sourceId, Guid.NewGuid())] : [new(sourceId, newId)],
            RecreatedFactureOuts = kind == "foreign-parent" ? [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())] : []
        };
        if (kind == "transport")
            fixture.PurchaseReturns.Exception = new MergeEgressException(MergeEgressException.PurchaseReturnUnknownResult, "Unknown result.", 503);
        var command = await fixture.CreateJobAsync();
        await fixture.Processor.ProcessAsync(command, CancellationToken.None);
        await fixture.Processor.ProcessAsync(command, CancellationToken.None);
        Assert.Single(fixture.PurchaseReturns.Calls);
        Assert.Empty(fixture.FactureOuts.Calls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var operation = await fixture.Db.MergeOperations.SingleAsync(item => item.OperationType == MergeOperationTypes.RecreatePurchaseReturns);
        Assert.Equal(MergeOperationStatuses.Failed, operation.Status);
        Assert.Equal(MergeEgressException.PurchaseReturnUnknownResult, operation.ErrorCode);
    }

    [Fact]
    public async Task ProcessAsync_RecreatesFactureInsAfterPurchaseReturnsAndBeforeArchivingDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var salesReturnId = Guid.NewGuid();
        var purchaseReturnId = Guid.NewGuid();
        var factureInId = Guid.NewGuid();
        var recreatedSalesReturnId = Guid.NewGuid();
        fixture.Documents.Documents =
        [
            new MoySkladDocumentReference("salesreturn", salesReturnId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("purchasereturn", purchaseReturnId, fixture.Duplicates[0].Id),
            new MoySkladDocumentReference("facturein", factureInId, fixture.Duplicates[0].Id)
        ];
        fixture.SalesReturns.Response = (_, _) => SalesReturnResult(
            [salesReturnId],
            [new SalesReturnTransferredDocumentResponse(salesReturnId, recreatedSalesReturnId)]);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var salesCall = Assert.Single(fixture.SalesReturns.Calls);
        Assert.Equal([salesReturnId], salesCall.DocumentIds);
        var purchaseCall = Assert.Single(fixture.PurchaseReturns.Calls);
        Assert.Equal(fixture.Main.Id, purchaseCall.MainCounterpartyId);
        Assert.Equal("purchasereturn", purchaseCall.DocumentType);
        Assert.Equal([purchaseReturnId], purchaseCall.DocumentIds);
        var factureInCall = Assert.Single(fixture.FactureIns.Calls);
        Assert.Equal(fixture.Main.Id, factureInCall.MainCounterpartyId);
        Assert.Equal("facturein", factureInCall.DocumentType);
        Assert.Equal([factureInId], factureInCall.DocumentIds);
        Assert.Empty(fixture.DocumentChanges.Calls);
        Assert.Single(fixture.Egress.ArchiveCalls);
        var auditTypes = await fixture.Db.DocumentsInOperation
            .OrderBy(item => item.OperationType)
            .Select(item => item.OperationType)
            .ToListAsync();
        Assert.Equal(
            [
                DocumentsInOperationTypes.RecreateFactureIn,
                DocumentsInOperationTypes.RecreatePurchaseReturn,
                DocumentsInOperationTypes.RecreateSalesReturn
            ],
            auditTypes);
        fixture.Db.ChangeTracker.Clear();
        var operations = await fixture.Db.MergeOperations.OrderBy(item => item.Sequence).ToListAsync();
        Assert.Equal(
            [
                MergeOperationTypes.RecreateSalesReturns,
                MergeOperationTypes.RecreatePurchaseReturns,
                MergeOperationTypes.RecreateFactureIns,
                MergeOperationTypes.RecreateFactureOuts,
                MergeOperationTypes.ArchiveDuplicate
            ],
            operations.Where(item => item.Sequence >= 3).Select(item => item.OperationType));
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("created-with-errors")]
    [InlineData("uncreated-after-delete")]
    public async Task ProcessAsync_OnlyCreatedWithErrorsOrUnCreatedButDeletedFactureInOutcomesStopMerge(string outcome)
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var factureInId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("facturein", factureInId, fixture.Duplicates[0].Id)];
        var newDocumentId = Guid.NewGuid();
        fixture.FactureIns.Response = documentIds => outcome switch
        {
            "created-with-errors" => new FactureInRecreationResponse(
                [], [], [], [new FactureInCreatedWithErrorResponse(
                    factureInId, newDocumentId, "CreatedWithError", "FACTUREIN_CREATE_WITH_ERRORS", "Post-create step failed.")]),
            "uncreated-after-delete" => new FactureInRecreationResponse([], [])
            {
                UnCreatedButDeleted = [new(factureInId, "Failed", "FACTUREIN_CREATE_FAILED", "The replacement was not created.")]
            },
            _ => new FactureInRecreationResponse(
                [],
                documentIds,
                [new FactureInSkippedDocumentResponse(
                    factureInId, "Skipped", "FACTUREIN_BASE_DOCUMENT_MISSING", "Base document was not found.")])
        };
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        var blocksMerge = outcome != "skipped";
        Assert.Equal(blocksMerge ? 0 : 1, fixture.Egress.ArchiveCalls.Count);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(blocksMerge ? MergeJobStatuses.Failed : MergeJobStatuses.Completed, job.Status);
        Assert.Equal(blocksMerge ? MergeOperationStatuses.Failed : MergeOperationStatuses.Completed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.RecreateFactureIns).Status);
        var snapshot = await fixture.Db.CounterpartyDocuments.FirstOrDefaultAsync(item => item.DocumentType == "facturein");
        if (outcome == "uncreated-after-delete")
            Assert.Null(snapshot);
        else if (outcome == "created-with-errors")
        {
            Assert.Equal(newDocumentId, snapshot!.DocumentId);
            Assert.Equal(fixture.Main.Id, snapshot.CounterpartyId);
        }
        else
        {
            Assert.Equal(factureInId, snapshot!.DocumentId);
            Assert.Equal(fixture.Duplicates[0].Id, snapshot.CounterpartyId);
        }
    }

    [Fact]
    public async Task ProcessAsync_SalesReturnRecreationFailureDoesNotArchiveDuplicates()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var salesReturnId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("salesreturn", salesReturnId, fixture.Duplicates[0].Id)];
        fixture.SalesReturns.Exception = new MergeEgressException(
            "SALESRETURN_RECREATION_FAILED", "Salesreturn recreation failed.", 400);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Empty(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.RecreateSalesReturns).Status);
        Assert.Empty(fixture.PurchaseReturns.Calls);
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
        var additionalData = await fixture.Db.DocumentAdditionalCommissions
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
        var audit = await fixture.Db.DocumentsInOperation.SingleAsync();
        Assert.Equal(DocumentsInOperationTypes.ChangeAgentAndContract, audit.OperationType);
        using var responses = JsonDocument.Parse(audit.RawJson);
        Assert.Equal(2, responses.RootElement.GetArrayLength());
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
        Assert.Empty(await fixture.Db.MergeCounterpartyLocks.ToListAsync());
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
        Assert.Equal(2, await fixture.Db.MergeCounterpartyLocks.CountAsync());
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
        Assert.Empty(await fixture.Db.MergeCounterpartyLocks.ToListAsync());
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
    public async Task ProcessAsync_SkippedCounterpartyPutStopsMergeBeforeRecreationAndArchive()
    {
        await using var fixture = await Fixture.CreateAsync(duplicateCount: 1);
        var demandId = Guid.NewGuid();
        fixture.Documents.Documents =
        [new MoySkladDocumentReference("demand", demandId, fixture.Duplicates[0].Id)];
        fixture.DocumentChanges.Response = (mainId, requested) => new MoySkladDocumentChangeCounterpartyResponse(
            mainId,
            requested.Count,
            0,
            requested.Count,
            0,
            [],
            requested.Select(item => new MoySkladDocumentChangeSkippedItem(
                item.DocumentType, item.DocumentId, "DOCUMENT_TYPE_NOT_CONFIGURED")).ToArray(),
            []);
        var command = await fixture.CreateJobAsync();

        await fixture.Processor.ProcessAsync(command, CancellationToken.None);

        Assert.Empty(fixture.SalesReturns.Calls);
        Assert.Empty(fixture.Egress.ArchiveCalls);
        fixture.Db.ChangeTracker.Clear();
        var job = await fixture.Db.MergeJobs.Include(item => item.Operations).SingleAsync();
        Assert.Equal(MergeJobStatuses.Failed, job.Status);
        Assert.Equal(MergeOperationStatuses.Failed,
            job.Operations.Single(item => item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties).Status);
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
        Assert.Equal(7, job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.ArchiveDuplicate).Sequence);
        Assert.Equal(MergeJobStatuses.Completed, job.Status);
    }

    private sealed class Fixture(
        SqliteConnection connection,
        CatalogSyncDbContext db,
        FakeMergeEgressClient egress,
        FakeDocumentDiscoveryEgressClient documents,
        FakeDocumentChangeEgressClient documentChanges,
        FakeSalesReturnRecreationSender salesReturns,
        FakePurchaseReturnRecreationSender purchaseReturns,
        FakeFactureInRecreationSender factureIns,
        FakeFactureOutRecreationSender factureOuts,
        int maxAttempts) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        public SqliteConnection Connection { get; } = connection;
        public CatalogSyncDbContext Db { get; } = db;
        public FakeMergeEgressClient Egress { get; } = egress;
        public FakeDocumentDiscoveryEgressClient Documents { get; } = documents;
        public FakeDocumentChangeEgressClient DocumentChanges { get; } = documentChanges;
        public FakeSalesReturnRecreationSender SalesReturns { get; } = salesReturns;
        public FakePurchaseReturnRecreationSender PurchaseReturns { get; } = purchaseReturns;
        public FakeFactureInRecreationSender FactureIns { get; } = factureIns;
        public FakeFactureOutRecreationSender FactureOuts { get; } = factureOuts;
        public Guid AccountId { get; } = Guid.NewGuid();
        public Counterparty Main { get; private set; } = null!;
        public List<Counterparty> Duplicates { get; } = [];
        public MergeProcessor Processor
        {
            get
            {
                var repository = new MergeRepository(Db);
                var snapshots = new DocumentSnapshotRepository(Db);
                var counterparties = new CounterpartyRepository(Db);
                var parser = new MoySkladCounterpartyParser();
                var normalizer = new CounterpartyNormalizer();
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Merge:MaxOperationAttempts"] = maxAttempts.ToString()
                }).Build();
                var state = new MergeOperationStateService(repository, TimeProvider.System, configuration,
                    NullLogger<MergeOperationStateService>.Instance);
                return new MergeProcessor(
                    new MergeCommandValidator(repository, TimeProvider.System),
                    state,
                    new MergeDocumentDiscoveryService(Documents, snapshots, TimeProvider.System,
                        MergeDocumentExclusionOptions.Parse(null),
                        NullLogger<MergeDocumentDiscoveryService>.Instance),
                    new MergeMainCounterpartyUpdateService(Egress, counterparties, parser, normalizer, TimeProvider.System),
                    new MergeDocumentChangeService(
                        snapshots, DocumentChanges, new DocumentsInOperationRepository(Db), TimeProvider.System),
                    SalesReturns,
                    PurchaseReturns,
                    FactureIns,
                    FactureOuts,
                    snapshots,
                    new DocumentsInOperationRepository(Db),
                    new MergeCounterpartyArchiveService(Egress, counterparties, parser, normalizer, TimeProvider.System),
                    NullLogger<MergeProcessor>.Instance);
            }
        }

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
            var salesReturns = new FakeSalesReturnRecreationSender();
            var purchaseReturns = new FakePurchaseReturnRecreationSender();
            var factureIns = new FakeFactureInRecreationSender();
            var factureOuts = new FakeFactureOutRecreationSender();
            var fixture = new Fixture(connection, db, egress, documents, documentChanges, salesReturns, purchaseReturns, factureIns, factureOuts, maxAttempts);
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
            var creator = new MergeJobCreator(new MergeRepository(Db), new CounterpartyRepository(Db), TimeProvider.System);
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
        public MergeEgressHttpResponse? CounterpartyRawResponse { get; set; }
        public MergeEgressHttpResponse? AgentAndContractRawResponse { get; set; }

        public Task<MergeEgressHttpResponse> ChangeCounterpartyAsync(
            Guid accountId, Guid mainCounterpartyId, IReadOnlyList<MoySkladDocumentChangeItem> documents,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add(documents.ToArray());
            CallOrder.Add("counterparty");
            if (CounterpartyRawResponse is not null)
                return Task.FromResult(CounterpartyRawResponse);
            var response = Response?.Invoke(mainCounterpartyId, documents) ??
                new MoySkladDocumentChangeCounterpartyResponse(
                    mainCounterpartyId, documents.Count, documents.Count, 0, 0,
                    documents.ToArray(), [], []);
            return Task.FromResult(EgressResponse(response));
        }

        public Task<MergeEgressHttpResponse> ChangeAgentAndContractAsync(
            Guid accountId, Guid mainCounterpartyId,
            IReadOnlyList<MoySkladDocumentChangeAgentAndContractItem> documents,
            Guid mergeJobId, Guid operationId, Guid userId, Guid correlationId,
            CancellationToken cancellationToken)
        {
            AgentAndContractCalls.Add(documents.ToArray());
            CallOrder.Add("agent-and-contract");
            if (AgentAndContractRawResponse is not null)
                return Task.FromResult(AgentAndContractRawResponse);
            var changed = documents.Select(item => new MoySkladDocumentChangeItem(
                item.DocumentType, item.DocumentId)).ToArray();
            var response = AgentAndContractResponse?.Invoke(mainCounterpartyId, documents) ??
                new MoySkladDocumentChangeCounterpartyResponse(
                    mainCounterpartyId, documents.Count, changed.Length, 0, 0, changed, [], []);
            return Task.FromResult(EgressResponse(response));
        }
    }

    private sealed class FakeSalesReturnRecreationSender : ISalesReturnRecreationSender
    {
        public List<(Guid MainAgentId, string DocumentType, IReadOnlyList<Guid> DocumentIds)> Calls { get; } = [];
        public List<string> CallOrder { get; } = [];
        public Func<Guid, IReadOnlyList<Guid>, MergeEgressHttpResponse>? Response { get; set; }
        public Exception? Exception { get; set; }

        public Task<MergeEgressHttpResponse> SendAsync(
            Guid accountId,
            Guid mainAgentId,
            string documentType,
            IReadOnlyList<Guid> documentIds,
            Guid mergeJobId,
            Guid operationId,
            Guid userId,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add((mainAgentId, documentType, documentIds.ToArray()));
            CallOrder.Add("salesreturn");
            if (Exception is not null)
                return Task.FromException<MergeEgressHttpResponse>(Exception);

            return Task.FromResult(Response?.Invoke(mainAgentId, documentIds) ?? SalesReturnResult(
                documentIds,
                documentIds.Select(id => new SalesReturnTransferredDocumentResponse(id, Guid.NewGuid())).ToArray()));
        }
    }

    private sealed class FakePurchaseReturnRecreationSender : IPurchaseReturnRecreationSender
    {
        public List<(Guid MainCounterpartyId, string DocumentType, IReadOnlyList<Guid> DocumentIds)> Calls { get; } = [];
        public List<string> CallOrder { get; } = [];
        public Exception? Exception { get; set; }
        public Func<IReadOnlyList<Guid>, PurchaseReturnRecreationResponse>? Response { get; set; }

        public Task<MergeEgressHttpResponse> SendAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            string documentType,
            IReadOnlyList<Guid> documentIds,
            Guid mergeJobId,
            Guid operationId,
            Guid userId,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add((mainCounterpartyId, documentType, documentIds.ToArray()));
            CallOrder.Add("purchasereturn");
            return Exception is null
                ? Task.FromResult(EgressResponse(Response?.Invoke(documentIds) ?? DefaultResponse(documentIds)))
                : Task.FromException<MergeEgressHttpResponse>(Exception);
        }

        private static PurchaseReturnRecreationResponse DefaultResponse(IReadOnlyList<Guid> documentIds)
        {
            var mappings = documentIds.Select(id => new RecreatedPurchaseReturnResponse(id, Guid.NewGuid())).ToArray();
            return new PurchaseReturnRecreationResponse(documentIds, [])
            {
                TransferredDocuments = mappings.Select(item =>
                    new DocumentRecreationTransferredDocumentResponse(item.SourceDocumentId, item.NewDocumentId)).ToArray(),
                RecreatedPurchaseReturns = mappings
            };
        }
    }

    private sealed class FakeFactureInRecreationSender : IFactureInRecreationSender
    {
        public List<(Guid MainCounterpartyId, string DocumentType, IReadOnlyList<Guid> DocumentIds)> Calls { get; } = [];
        public Func<IReadOnlyList<Guid>, FactureInRecreationResponse>? Response { get; set; }
        public Exception? Exception { get; set; }

        public Task<MergeEgressHttpResponse> SendAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            string documentType,
            IReadOnlyList<Guid> documentIds,
            Guid mergeJobId,
            Guid operationId,
            Guid userId,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add((mainCounterpartyId, documentType, documentIds.ToArray()));
            if (Exception is not null)
                return Task.FromException<MergeEgressHttpResponse>(Exception);

            return Task.FromResult(EgressResponse(Response?.Invoke(documentIds) ?? new FactureInRecreationResponse(documentIds, [])
            {
                TransferredDocuments = documentIds
                    .Select(id => new DocumentRecreationTransferredDocumentResponse(id, Guid.NewGuid()))
                    .ToArray()
            }));
        }
    }

    private sealed class FakeFactureOutRecreationSender : IFactureOutRecreationSender
    {
        public List<(Guid MainCounterpartyId, string DocumentType, IReadOnlyList<Guid> DocumentIds)> Calls { get; } = [];
        public Func<IReadOnlyList<Guid>, FactureOutRecreationResponse>? Response { get; set; }

        public Task<MergeEgressHttpResponse> SendAsync(
            Guid accountId,
            Guid mainCounterpartyId,
            string documentType,
            IReadOnlyList<Guid> documentIds,
            Guid mergeJobId,
            Guid operationId,
            Guid userId,
            Guid correlationId,
            CancellationToken cancellationToken)
        {
            Calls.Add((mainCounterpartyId, documentType, documentIds.ToArray()));
            return Task.FromResult(EgressResponse(Response?.Invoke(documentIds) ?? new FactureOutRecreationResponse(documentIds, [], [], [], [])
            {
                TransferredDocuments = documentIds
                    .Select(id => new DocumentRecreationTransferredDocumentResponse(id, Guid.NewGuid()))
                    .ToArray()
            }));
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
        Id = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        AccountId = accountId,
        RequestedByUserId = Guid.NewGuid(),
        RequestedMode = "full",
        ExecutionMode = "full",
        Status = "completed",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Counterparty NewCounterparty(Guid accountId, SyncRun run, string name) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Name = name,
        NormalizedName = name.ToLowerInvariant(),
        RawJson = "{}",
        LastSyncRun = run,
        LastSyncRunId = run.Id,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
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
            }))
            { BaseAddress = new Uri("http://egress/") },
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["InternalApi:Key"] = "key" }).Build());

        var response = await client.ChangeAgentAndContractAsync(
            accountId, mainId, [document], Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.OK, response.StatusCode);
        var parsed = JsonSerializer.Deserialize<MoySkladDocumentChangeCounterpartyResponse>(
            response.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(parsed);
        Assert.Single(parsed!.ChangedDocuments);
    }

    [Fact]
    public async Task ChangeCounterpartyAsync_PreservesErrorStatusAndBody()
    {
        const string errorBody = "{\"code\":\"EGRESS_BUSY\",\"message\":\"Try again.\"}";
        var client = new DocumentChangeEgressClient(
            new HttpClient(new CallbackHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(errorBody)
            }))) { BaseAddress = new Uri("http://egress/") },
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["InternalApi:Key"] = "key" }).Build());

        var response = await client.ChangeCounterpartyAsync(
            Guid.NewGuid(), Guid.NewGuid(), [new MoySkladDocumentChangeItem("demand", Guid.NewGuid())],
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(errorBody, response.Body);
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
