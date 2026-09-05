using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;

namespace MsContractor.Sync.Tests;

public sealed class TenantDatabaseContextTests
{
    [Fact]
    public async Task SetTenantAsync_RejectsEmptyAccountId()
    {
        await using var fixture = await CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            fixture.Context.SetTenantAsync(Guid.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task SetTenantAsync_PreventsReusingContextForAnotherAccount()
    {
        await using var fixture = await CreateAsync();
        var accountId = Guid.NewGuid();

        await fixture.Context.SetTenantAsync(accountId, CancellationToken.None);
        await fixture.Context.SetTenantAsync(accountId, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Context.SetTenantAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task CounterpartyDocument_ReferencesCounterpartyInSameAccount()
    {
        await using var fixture = await CreateAsync();

        var entityType = fixture.Context.Model.FindEntityType(typeof(CounterpartyDocument))!;
        var foreignKey = Assert.Single(entityType.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Counterparty));

        Assert.Equal(["CounterpartyId", "AccountId"],
            foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(["Id", "AccountId"],
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void DocumentAdditionalCommission_ReferencesDocumentByIdWithCascadeDelete()
    {
        using var context = new CatalogSyncDbContext(
            new DbContextOptionsBuilder<CatalogSyncDbContext>().UseSqlite("Data Source=:memory:").Options);

        var entityType = context.Model.FindEntityType(typeof(DocumentAdditionalCommission))!;
        var foreignKey = Assert.Single(entityType.GetForeignKeys());

        Assert.Equal(["DocumentId"], foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(typeof(CounterpartyDocument), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(["DocumentId"], foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void DocumentAdditionalData_ReferencesDocumentByIdWithCascadeDelete()
    {
        using var context = new CatalogSyncDbContext(
            new DbContextOptionsBuilder<CatalogSyncDbContext>().UseSqlite("Data Source=:memory:").Options);

        var entityType = context.Model.FindEntityType(typeof(DocumentAdditionalData))!;
        var foreignKey = Assert.Single(entityType.GetForeignKeys());

        Assert.Equal(["DocumentId"], foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(typeof(CounterpartyDocument), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(["DocumentId"], foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    private static async Task<Fixture> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new CatalogSyncDbContext(
            new DbContextOptionsBuilder<CatalogSyncDbContext>()
                .UseSqlite(connection)
                .Options);
        await context.Database.EnsureCreatedAsync();
        return new Fixture(connection, context);
    }

    private sealed class Fixture(SqliteConnection connection, CatalogSyncDbContext context)
        : IAsyncDisposable
    {
        public CatalogSyncDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
