using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Persistence;
using Npgsql;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface ISalesReturnOperationRepository
{
    Task<IAsyncDisposable?> TryLockAccountAsync(Guid accountId, CancellationToken cancellationToken);
    Task<SalesReturnOperation?> GetAsync(Guid accountId, Guid operationId, CancellationToken cancellationToken);
    Task CreateAsync(SalesReturnOperation operation, CancellationToken cancellationToken);
    Task SaveAsync(SalesReturnOperation operation, CancellationToken cancellationToken);
    Task<IReadOnlyList<SalesReturnOperationKey>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class SalesReturnOperationRepository(EgressDbContext db) : ISalesReturnOperationRepository
{
    // Dedicated non-pooled session: closing it releases the advisory lock even on an exception.
    // This lease is not an EF transaction; journal commits stay short and precede HTTP writes.
    public async Task<IAsyncDisposable?> TryLockAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty) throw new ArgumentException("Account is required.", nameof(accountId));
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
            { Pooling = false }.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var key = BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(accountId.ToByteArray()));
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
            command.Parameters.AddWithValue("key", key);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return connection;
            await connection.DisposeAsync();
            return null;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<SalesReturnOperation?> GetAsync(Guid accountId, Guid operationId, CancellationToken cancellationToken)
    {
        var operation = await db.SalesReturnOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.AccountId == accountId && x.OperationId == operationId, cancellationToken);
        if (operation is not null)
        {
            operation.DeserializeItems();
            var payloads = await db.SalesReturnClaims.AsNoTracking()
                .Where(x => x.AccountId == accountId && x.OperationId == operationId)
                .ToDictionaryAsync(x => x.OldDocumentId, x => x.Payload, cancellationToken);
            foreach (var item in operation.Items) item.Payload = payloads[item.OldDocumentId];
        }
        return operation;
    }

    public async Task CreateAsync(SalesReturnOperation operation, CancellationToken cancellationToken)
    {
        var ids = operation.Items.Select(x => x.OldDocumentId).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.SalesReturnClaims.AnyAsync(x => x.AccountId == operation.AccountId && ids.Contains(x.OldDocumentId), cancellationToken))
            throw new EgressException(409, "SALESRETURN_ALREADY_CLAIMED", "A source document belongs to another recreation operation.");
        operation.SerializeItems();
        db.SalesReturnOperations.Add(operation);
        db.SalesReturnClaims.AddRange(operation.Items.Select(item => new SalesReturnClaim
            { AccountId = operation.AccountId, OldDocumentId = item.OldDocumentId, OperationId = operation.OperationId, Payload = item.Payload }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task SaveAsync(SalesReturnOperation operation, CancellationToken cancellationToken)
    {
        operation.SerializeItems();
        var count = await db.SalesReturnOperations
            .Where(x => x.AccountId == operation.AccountId && x.OperationId == operation.OperationId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ItemsJson, operation.ItemsJson)
                .SetProperty(x => x.NextAttemptAt, operation.NextAttemptAt)
                .SetProperty(x => x.Attempts, operation.Attempts)
                .SetProperty(x => x.UpdatedAt, operation.UpdatedAt), cancellationToken);
        if (count != 1) throw new InvalidOperationException("Operation does not exist in this account.");
    }

    // Explicit infrastructure scan; returns only account/operation keys, never tenant payloads.
    public async Task<IReadOnlyList<SalesReturnOperationKey>> GetDueAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await db.SalesReturnOperations.AsNoTracking().Where(x => x.NextAttemptAt <= now)
            .OrderBy(x => x.NextAttemptAt).Take(100)
            .Select(x => new SalesReturnOperationKey(x.AccountId, x.OperationId)).ToListAsync(cancellationToken);
}
