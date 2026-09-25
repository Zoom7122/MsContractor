using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsContractor.MoySkladEgressService.Gateways.Documents.Factureout;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;
using MsContractor.MoySkladEgressService.Repositories;
using MsContractor.MoySkladEgressService.Services.Documents.Factureout;

namespace MsContractor.Sync.Tests;

public sealed class FactureOutPreparationServiceTests
{
    [Fact]
    public async Task PrepareAsync_SavesPreparedAndSkippedRows_AndResetsExistingState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new EgressDbContext(
            new DbContextOptionsBuilder<EgressDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var sourceSyncId = Guid.NewGuid();
        var oldNewSyncId = Guid.NewGuid();
        var otherAccountSyncId = Guid.NewGuid();
        dbContext.FactureOutRecreationItems.AddRange(
            new FactureOutRecreationItem
            {
                AccountId = accountId,
                SourceDocumentId = documentId,
                NewSyncId = oldNewSyncId,
                NewDocumentId = Guid.NewGuid(),
                Status = FactureOutRecreationStatuses.CreatePayload
            },
            new FactureOutRecreationItem
            {
                AccountId = accountId,
                SourceDocumentId = missingId,
                NewSyncId = Guid.NewGuid(),
                Status = FactureOutRecreationStatuses.CreatePayload
            },
            new FactureOutRecreationItem
            {
                AccountId = otherAccountId,
                SourceDocumentId = documentId,
                NewSyncId = otherAccountSyncId,
                Status = FactureOutRecreationStatuses.CreatePayload
            });
        await dbContext.SaveChangesAsync();

        var rawJson = new JsonObject
        {
            ["id"] = documentId.ToString("D"),
            ["syncId"] = sourceSyncId.ToString("D"),
            ["demands"] = new JsonArray(new JsonObject
            {
                ["meta"] = new JsonObject
                {
                    ["href"] = $"https://api.test/entity/demand/{Guid.NewGuid():D}",
                    ["type"] = "demand",
                    ["mediaType"] = "application/json"
                }
            })
        }.ToJsonString();
        var service = new FactureOutPreparationService(
            new RecordingGateway(new Dictionary<Guid, string> { [documentId] = rawJson }),
            new FactureOutRawDataRepository(dbContext),
            new FactureOutRecreationItemRepository(dbContext),
            NullLogger<FactureOutPreparationService>.Instance);

        var result = await service.PrepareAsync(
            accountId,
            [documentId, missingId],
            CancellationToken.None);

        var prepared = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == accountId && item.SourceDocumentId == documentId);
        Assert.Equal(sourceSyncId, prepared.SourceSyncId);
        Assert.Null(prepared.NewSyncId);
        Assert.Null(prepared.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.Prepared, prepared.Status);

        var skipped = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == accountId && item.SourceDocumentId == missingId);
        Assert.Null(skipped.SourceSyncId);
        Assert.Null(skipped.NewSyncId);
        Assert.Null(skipped.NewDocumentId);
        Assert.Equal(FactureOutRecreationStatuses.Skipped, skipped.Status);

        var otherAccount = await dbContext.FactureOutRecreationItems
            .SingleAsync(item => item.AccountId == otherAccountId && item.SourceDocumentId == documentId);
        Assert.Equal(otherAccountSyncId, otherAccount.NewSyncId);
        Assert.Equal(FactureOutRecreationStatuses.CreatePayload, otherAccount.Status);
        Assert.Equal([documentId], result.DocumentIds);
        Assert.Equal(missingId, Assert.Single(result.SkippedDocuments).DocumentId);
    }

    [Fact]
    public async Task PrepareAsync_PreparesDocumentsWithAnyNonEmptyBaseAndSkipsDocumentsWithoutOne()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var dbContext = new EgressDbContext(
            new DbContextOptionsBuilder<EgressDbContext>().UseSqlite(connection).Options);
        await dbContext.Database.EnsureCreatedAsync();

        var accountId = Guid.NewGuid();
        var documents = new Dictionary<Guid, string>();
        var sourceSyncIds = new Dictionary<Guid, Guid>();
        var preparedIds = new List<Guid>();
        var withoutBaseIds = new List<Guid>();

        foreach (var (field, type) in new[]
                 {
                     ("demands", "demand"),
                     ("returns", "purchasereturn"),
                     ("payments", "paymentin"),
                     ("payments", "cashin")
                 })
        {
            AddDocument(RelationCollection(field, type, useRows: false), preparedIds);
            AddDocument(RelationCollection(field, type, useRows: true), preparedIds);
        }

        AddDocument(new JsonObject(), withoutBaseIds);
        AddDocument(new JsonObject
        {
            ["demands"] = null,
            ["returns"] = null,
            ["payments"] = null
        }, withoutBaseIds);
        AddDocument(new JsonObject
        {
            ["demands"] = new JsonArray(),
            ["returns"] = new JsonArray(),
            ["payments"] = new JsonArray()
        }, withoutBaseIds);
        AddDocument(new JsonObject
        {
            ["demands"] = new JsonObject { ["rows"] = new JsonArray() }
        }, withoutBaseIds);
        AddDocument(new JsonObject
        {
            ["demands"] = "not-a-collection",
            ["returns"] = new JsonObject { ["rows"] = "not-an-array" },
            ["payments"] = new JsonArray(JsonValue.Create("not-a-reference"))
        }, withoutBaseIds);

        var result = await CreateService(dbContext, documents).PrepareAsync(
            accountId,
            documents.Keys.ToArray(),
            CancellationToken.None);

        Assert.Equal(preparedIds.Order(), result.DocumentIds.Order());
        Assert.Equal(withoutBaseIds.Order(), result.SkippedDocuments.Select(item => item.DocumentId).Order());
        Assert.All(result.SkippedDocuments, item =>
        {
            Assert.Equal("Skipped", item.Status);
            Assert.Equal("FACTUREOUT_BASE_MISSING", item.ErrorCode);
            Assert.Contains("basis", item.Reason, StringComparison.OrdinalIgnoreCase);
        });

        var rawDocuments = await dbContext.FactureOutRawData
            .Where(item => item.AccountId == accountId)
            .ToDictionaryAsync(item => item.DocumentId, item => item.RawJson);
        Assert.Equal(documents.Count, rawDocuments.Count);

        var recreationItems = await dbContext.FactureOutRecreationItems
            .Where(item => item.AccountId == accountId)
            .ToDictionaryAsync(item => item.SourceDocumentId);
        foreach (var documentId in preparedIds)
        {
            Assert.Equal(sourceSyncIds[documentId], recreationItems[documentId].SourceSyncId);
            Assert.Equal(FactureOutRecreationStatuses.Prepared, recreationItems[documentId].Status);
        }

        foreach (var documentId in withoutBaseIds)
        {
            var item = recreationItems[documentId];
            Assert.Equal(sourceSyncIds[documentId], item.SourceSyncId);
            Assert.Null(item.NewSyncId);
            Assert.Null(item.NewDocumentId);
            Assert.Equal(FactureOutRecreationStatuses.Skipped, item.Status);
        }

        void AddDocument(JsonObject relationFields, ICollection<Guid> category)
        {
            var documentId = Guid.NewGuid();
            var sourceSyncId = Guid.NewGuid();
            var raw = new JsonObject
            {
                ["id"] = documentId.ToString("D"),
                ["syncId"] = sourceSyncId.ToString("D")
            };
            foreach (var relation in relationFields)
                raw[relation.Key] = relation.Value?.DeepClone();

            documents.Add(documentId, raw.ToJsonString());
            sourceSyncIds.Add(documentId, sourceSyncId);
            category.Add(documentId);
        }
    }

    private static FactureOutPreparationService CreateService(
        EgressDbContext dbContext,
        IReadOnlyDictionary<Guid, string> documents) =>
        new(
            new RecordingGateway(documents),
            new FactureOutRawDataRepository(dbContext),
            new FactureOutRecreationItemRepository(dbContext),
            NullLogger<FactureOutPreparationService>.Instance);

    private static JsonObject RelationCollection(string field, string type, bool useRows)
    {
        var reference = new JsonObject
        {
            ["meta"] = new JsonObject
            {
                ["href"] = $"https://api.test/entity/{type}/{Guid.NewGuid():D}",
                ["type"] = type,
                ["mediaType"] = "application/json"
            }
        };
        var rows = new JsonArray(reference);
        JsonNode collection = useRows ? new JsonObject { ["rows"] = rows } : rows;
        return new JsonObject { [field] = collection };
    }

    private sealed class RecordingGateway(IReadOnlyDictionary<Guid, string> documents)
        : IMoySkladFactureOutGateway
    {
        public Task<IReadOnlyDictionary<Guid, string>> GetAsync(
            Guid accountId,
            string correlationId,
            IReadOnlyList<Guid> factureOutIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                documents.Where(item => factureOutIds.Contains(item.Key))
                    .ToDictionary(item => item.Key, item => item.Value));
    }
}
