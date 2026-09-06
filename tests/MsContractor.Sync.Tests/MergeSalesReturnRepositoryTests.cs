using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.Sync.Tests;

public sealed class MergeSalesReturnRepositoryTests
{
    [SalesReturnPostgresFact]
    public async Task ReplacementRollsBackOldRowsOnFailureAndIsTenantScopedAndReplaySafe()
    {
        await using var fixture = await SalesReturnRecreationTests.Fixture.CreateAsync();
        await using var egress = fixture.Db();
        var options = new DbContextOptionsBuilder<CatalogSyncDbContext>().UseNpgsql(egress.Database.GetConnectionString()).Options;
        await using var db = new CatalogSyncDbContext(options);
        await db.Database.MigrateAsync();
        var account = Guid.NewGuid(); var otherAccount = Guid.NewGuid();
        var run = new SyncRun { Id = Guid.NewGuid(), AccountId = account, MessageId = Guid.NewGuid() };
        var otherRun = new SyncRun { Id = Guid.NewGuid(), AccountId = otherAccount, MessageId = Guid.NewGuid() };
        var duplicate = new Counterparty { Id = Guid.NewGuid(), AccountId = account, LastSyncRun = run };
        var main = new Counterparty { Id = Guid.NewGuid(), AccountId = account, LastSyncRun = run };
        var foreign = new Counterparty { Id = Guid.NewGuid(), AccountId = otherAccount, LastSyncRun = otherRun };
        db.AddRange(duplicate, main, foreign);
        await db.SaveChangesAsync();
        var oldId = Guid.NewGuid(); var otherId = Guid.NewGuid(); var newId = Guid.NewGuid();
        db.CounterpartyDocuments.AddRange(
            new CounterpartyDocument { AccountId = account, CounterpartyId = duplicate.Id, DocumentType = "salesreturn", DocumentId = oldId },
            new CounterpartyDocument { AccountId = otherAccount, CounterpartyId = foreign.Id, DocumentType = "salesreturn", DocumentId = otherId });
        db.DocumentAdditionalData.AddRange(new DocumentAdditionalData { DocumentId = oldId, RawJson = "{\"name\":\"original\"}" },
            new DocumentAdditionalData { DocumentId = otherId, RawJson = "{}" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new DocumentSnapshotRepository(db);
        var data = await repository.GetAdditionalDataAsync(account, [oldId, otherId], default);
        Assert.Equal(oldId, Assert.Single(data).Key);
        var invalid = new RecreatedDocumentSnapshot(oldId, newId, duplicate.Id, "invalid-json", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateException>(() => repository.ApplyRecreatedAsync(account, main.Id, [invalid], default));
        db.ChangeTracker.Clear();
        Assert.True(await db.CounterpartyDocuments.AnyAsync(x => x.DocumentId == oldId));
        Assert.True(await db.DocumentAdditionalData.AnyAsync(x => x.DocumentId == oldId));
        Assert.False(await db.CounterpartyDocuments.AnyAsync(x => x.DocumentId == newId));
        var valid = invalid with { RawJson = "{\"name\":\"replacement\"}" };
        await repository.ApplyRecreatedAsync(account, main.Id, [valid], default);
        await repository.ApplyRecreatedAsync(account, main.Id, [valid], default);
        Assert.False(await db.DocumentAdditionalData.AnyAsync(x => x.DocumentId == oldId));
        Assert.Equal(main.Id, (await db.CounterpartyDocuments.SingleAsync(x => x.DocumentId == newId)).CounterpartyId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ApplyRecreatedAsync(account, main.Id,
            [new RecreatedDocumentSnapshot(otherId, Guid.NewGuid(), foreign.Id, "{}", DateTimeOffset.UtcNow)], default));
        db.ChangeTracker.Clear();
        Assert.True(await db.CounterpartyDocuments.AnyAsync(x => x.AccountId == otherAccount && x.DocumentId == otherId));
        Assert.Contains("20260906125521_PersistSalesReturnRecreationRequest", await db.Database.GetAppliedMigrationsAsync());
    }
}
