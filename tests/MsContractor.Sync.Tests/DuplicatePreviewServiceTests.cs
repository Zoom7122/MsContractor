using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.CatalogSyncService.Models;
using MsContractor.CatalogSyncService.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.Contracts.Duplicates;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.Sync.Tests;

public sealed class DuplicatePreviewServiceTests
{
    [Fact]
    public async Task FindAsync_IncludesArchivedOnlyWhenDocumentsExistAndKeepsAccountScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateContextAsync(connection);
        var account = Guid.NewGuid();
        var other = Guid.NewGuid();
        var accountRun = SyncRun(account);
        var otherRun = SyncRun(other);
        db.SyncRuns.AddRange(accountRun, otherRun);
        var now = DateTimeOffset.Parse("2026-08-05T12:00:00Z");
        var a = Counterparty(account, "A", "same-name", "a@example.test", null, now.AddMinutes(-3));
        var b = Counterparty(account, "B", "same-name", "b@example.test", null, now.AddMinutes(-2));
        var c = Counterparty(account, "C", "unique", "a@example.test", null, now.AddMinutes(-1));
        var archived = Counterparty(account, "Archived", "same-name", null, null, now);
        archived.Archived = true;
        var archivedWithoutDocuments = Counterparty(account, "Archived without documents", "same-name", null, null, now.AddSeconds(1));
        archivedWithoutDocuments.Archived = true;
        var foreign = Counterparty(other, "Foreign", "same-name", null, null, now);
        foreach (var counterparty in new[] { a, b, c, archived, archivedWithoutDocuments })
            counterparty.LastSyncRunId = accountRun.Id;
        foreign.LastSyncRunId = otherRun.Id;
        db.Counterparties.AddRange(a, b, c, archived, archivedWithoutDocuments, foreign);
        db.CounterpartyDocuments.Add(new CounterpartyDocument
        {
            AccountId = account,
            CounterpartyId = archived.Id,
            DocumentType = "purchasereturn",
            DocumentId = Guid.NewGuid(),
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        var result = await new DuplicatePreviewService(new CounterpartyRepository(db)).FindAsync(
            account, [DuplicateMatchField.Name, DuplicateMatchField.Email], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, group => group.MatchedBy == "name" && group.MatchValue == "same-name" &&
            group.Counterparties.Select(item => item.Id).ToHashSet().SetEquals(new[] { a.Id, b.Id, archived.Id }));
        Assert.Contains(result, group => group.MatchedBy == "email" && group.MatchValue == "a@example.test" &&
            group.Counterparties.Select(item => item.Id).SequenceEqual(new[] { a.Id, c.Id }));
        Assert.All(result.SelectMany(group => group.Counterparties), item => Assert.NotEqual(foreign.Id, item.Id));
        Assert.DoesNotContain(result.SelectMany(group => group.Counterparties), item => item.Id == archivedWithoutDocuments.Id);
        Assert.Equal(6, await db.Counterparties.CountAsync());
    }

    [Fact]
    public async Task FindAsync_DeduplicatesIdenticalGroupsAndReturnsJsonObjectForInvalidRawJson()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateContextAsync(connection);
        var account = Guid.NewGuid();
        var run = SyncRun(account);
        db.SyncRuns.Add(run);
        var a = Counterparty(account, "A", "same", "same", "+700", DateTimeOffset.UtcNow);
        a.RawJson = "not-json";
        var b = Counterparty(account, "B", "same", "same", "+700", DateTimeOffset.UtcNow.AddMinutes(1));
        a.LastSyncRunId = run.Id;
        b.LastSyncRunId = run.Id;
        db.Counterparties.AddRange(a, b);
        await db.SaveChangesAsync();

        var result = await new DuplicatePreviewService(new CounterpartyRepository(db)).FindAsync(
            account, [DuplicateMatchField.Name, DuplicateMatchField.Email, DuplicateMatchField.Phone], CancellationToken.None);

        var group = Assert.Single(result);
        Assert.Equal("name", group.MatchedBy);
        Assert.Equal("same", group.MatchValue);
        Assert.Equal(2, group.Counterparties.Count);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, group.Counterparties[0].RawJson.ValueKind);
    }

    [Fact]
    public async Task FindAsync_ExcludesLockedCounterpartiesBeforeGrouping()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateContextAsync(connection);
        var account = Guid.NewGuid();
        var run = SyncRun(account);
        var a = Counterparty(account, "A", "same-name", "shared@example.test", null, DateTimeOffset.UtcNow);
        var b = Counterparty(account, "B", "same-name", null, null, DateTimeOffset.UtcNow);
        var c = Counterparty(account, "C", "other-name", "shared@example.test", null, DateTimeOffset.UtcNow);
        foreach (var counterparty in new[] { a, b, c }) counterparty.LastSyncRunId = run.Id;
        var job = new MergeJob { Id = Guid.NewGuid(), AccountId = account, MessageId = Guid.NewGuid(),
            MainCounterpartyId = b.Id, Payload = "{}" };
        db.AddRange(run, a, b, c, job,
            new MergeCounterpartyLock { AccountId = account, CounterpartyId = b.Id, MergeJob = job,
                MergeJobId = job.Id, AcquiredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        var groups = await new DuplicatePreviewService(new CounterpartyRepository(db)).FindAsync(
            account, [DuplicateMatchField.Name, DuplicateMatchField.Email], CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal("email", group.MatchedBy);
        Assert.Equal(new[] { a.Id, c.Id }.Order(), group.Counterparties.Select(item => item.Id).Order());
    }

    [Fact]
    public async Task FindAsync_FiltersCandidatesByAllNormalizedAccountExclusionsBeforeGrouping()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = await CreateContextAsync(connection);
        var account = Guid.NewGuid();
        var otherAccount = Guid.NewGuid();
        var run = SyncRun(account);
        var now = DateTimeOffset.UtcNow;
        var keepA = Counterparty(account, "Keep A", "keep group", "shared@example.test", null, now);
        var keepB = Counterparty(account, "Keep B", "keep group", "other@example.test", null, now.AddMinutes(1));
        var excludedByNameA = Counterparty(account, "Name A", "name group", "name-a@example.test", null, now);
        var excludedByNameB = Counterparty(account, "Name B", "name group", "name-b@example.test", null, now.AddMinutes(1));
        var excludedByEmailA = Counterparty(account, "Email A", "email group", "blocked@example.test", null, now);
        var excludedByEmailB = Counterparty(account, "Email B", "email group", "allowed@example.test", null, now.AddMinutes(1));
        var excludedByPhoneA = Counterparty(account, "Phone A", "phone group", "phone-a@example.test", "+7 999 123-45-67", now);
        var excludedByPhoneB = Counterparty(account, "Phone B", "phone group", "phone-b@example.test", "+7 999 765-43-21", now.AddMinutes(1));
        foreach (var candidate in new[]
                 {
                     keepA, keepB, excludedByNameA, excludedByNameB, excludedByEmailA,
                     excludedByEmailB, excludedByPhoneA, excludedByPhoneB
                 })
        {
            candidate.LastSyncRunId = run.Id;
        }

        db.SyncRuns.Add(run);
        db.Counterparties.AddRange(
            keepA, keepB, excludedByNameA, excludedByNameB, excludedByEmailA,
            excludedByEmailB, excludedByPhoneA, excludedByPhoneB);
        db.CatalogSettings.AddRange(
            SettingsRow(account, now),
            SettingsRow(otherAccount, now));
        db.CatalogSettingExclusions.AddRange(
            new CatalogSettingExclusion { AccountId = account, Field = "name", Value = "  NAME   GROUP " },
            new CatalogSettingExclusion { AccountId = account, Field = "email", Value = " BLOCKED@EXAMPLE.TEST " },
            new CatalogSettingExclusion { AccountId = account, Field = "phone", Value = "8 999 123-45-67" },
            new CatalogSettingExclusion { AccountId = otherAccount, Field = "email", Value = "shared@example.test" });
        await db.SaveChangesAsync();

        var groups = await new DuplicatePreviewService(new CounterpartyRepository(db)).FindAsync(
            account, [DuplicateMatchField.Name], CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal("keep group", group.MatchValue);
        Assert.Equal(2, group.Counterparties.Count);
        Assert.True(new[] { keepA.Id, keepB.Id }.ToHashSet()
            .SetEquals(group.Counterparties.Select(item => item.Id)));
        Assert.DoesNotContain(groups.SelectMany(item => item.Counterparties), item =>
            new[] { excludedByNameA.Id, excludedByNameB.Id, excludedByEmailA.Id,
                    excludedByEmailB.Id, excludedByPhoneA.Id, excludedByPhoneB.Id }
                .Contains(item.Id));
    }

    private static async Task<CatalogSyncDbContext> CreateContextAsync(SqliteConnection connection)
    {
        var context = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static Counterparty Counterparty(Guid accountId, string name, string normalizedName, string? normalizedEmail, string? normalizedPhone, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        Name = name,
        NormalizedName = normalizedName,
        NormalizedEmail = normalizedEmail,
        NormalizedPhone = normalizedPhone,
        RawJson = "{}",
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private static CatalogSettings SettingsRow(Guid accountId, DateTimeOffset updatedAt) => new()
    {
        AccountId = accountId,
        IncludeArchivedWithDocuments = true,
        GroupLimit = 200,
        ItemLimit = 200,
        UpdatedAt = updatedAt
    };

    private static SyncRun SyncRun(Guid accountId) => new()
    {
        Id = Guid.NewGuid(),
        MessageId = Guid.NewGuid(),
        AccountId = accountId,
        RequestedByUserId = Guid.NewGuid(),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
