using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Duplicates;
using MsContractor.DuplicatesMergeService.Services;

namespace MsContractor.Sync.Tests;

public sealed class DuplicatePreviewServiceTests
{
    [Fact]
    public async Task FindAsync_ScopesAccountExcludesArchivedAndKeepsDistinctOverlappingGroups()
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
        var foreign = Counterparty(other, "Foreign", "same-name", null, null, now);
        foreach (var counterparty in new[] { a, b, c, archived })
            counterparty.LastSyncRunId = accountRun.Id;
        foreign.LastSyncRunId = otherRun.Id;
        db.Counterparties.AddRange(a, b, c, archived, foreign);
        await db.SaveChangesAsync();

        var result = await new DuplicatePreviewService(db).FindAsync(
            account, [DuplicateMatchField.Name, DuplicateMatchField.Email], CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, group => group.MatchedBy == "name" && group.MatchValue == "same-name" &&
            group.Counterparties.Select(item => item.Id).SequenceEqual(new[] { a.Id, b.Id }));
        Assert.Contains(result, group => group.MatchedBy == "email" && group.MatchValue == "a@example.test" &&
            group.Counterparties.Select(item => item.Id).SequenceEqual(new[] { a.Id, c.Id }));
        Assert.All(result.SelectMany(group => group.Counterparties), item => Assert.NotEqual(foreign.Id, item.Id));
        Assert.DoesNotContain(result.SelectMany(group => group.Counterparties), item => item.Id == archived.Id);
        Assert.Equal(5, await db.Counterparties.CountAsync());
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

        var result = await new DuplicatePreviewService(db).FindAsync(
            account, [DuplicateMatchField.Name, DuplicateMatchField.Email, DuplicateMatchField.Phone], CancellationToken.None);

        var group = Assert.Single(result);
        Assert.Equal("name", group.MatchedBy);
        Assert.Equal("same", group.MatchValue);
        Assert.Equal(2, group.Counterparties.Count);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, group.Counterparties[0].RawJson.ValueKind);
    }

    private static async Task<CatalogSyncDbContext> CreateContextAsync(SqliteConnection connection)
    {
        var context = new CatalogSyncDbContext(new DbContextOptionsBuilder<CatalogSyncDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        return context;
    }

    private static Counterparty Counterparty(Guid accountId, string name, string normalizedName, string? normalizedEmail, string? normalizedPhone, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(), AccountId = accountId, Name = name, NormalizedName = normalizedName,
        NormalizedEmail = normalizedEmail, NormalizedPhone = normalizedPhone, RawJson = "{}",
        CreatedAt = createdAt, UpdatedAt = createdAt
    };

    private static SyncRun SyncRun(Guid accountId) => new()
    {
        Id = Guid.NewGuid(), MessageId = Guid.NewGuid(), AccountId = accountId,
        RequestedByUserId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}
