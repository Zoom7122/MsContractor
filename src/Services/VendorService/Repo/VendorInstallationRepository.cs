using Microsoft.EntityFrameworkCore;
using Npgsql;
using MsContractor.VendorService.Services.Exceptions;

namespace MsContractor.VendorService.Repo;

public sealed record SaveVendorInstallationCommand(
    string RequestId,
    Installation Installation,
    OutboxMessage OutboxMessage);

public sealed record SaveVendorInstallationResult(Installation Installation, bool IdempotentReplay);

public sealed class VendorInstallationRepository(VendorDbContext dbContext)
{
    public Task<Installation?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        dbContext.Installations.SingleOrDefaultAsync(item => item.AccountId == accountId, cancellationToken);

    public async Task<SaveVendorInstallationResult> SaveAsync(
        SaveVendorInstallationCommand command,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var priorMessage = await dbContext.OutboxMessages.AsNoTracking()
            .SingleOrDefaultAsync(message => message.RequestId == command.RequestId, cancellationToken);
        if (priorMessage is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            EnsureCompatible(priorMessage, command.OutboxMessage);
            return new SaveVendorInstallationResult(
                await GetExistingInstallationAsync(priorMessage.AccountId, cancellationToken), true);
        }

        try
        {
            if (dbContext.Entry(command.Installation).State == EntityState.Detached)
                dbContext.Installations.Add(command.Installation);
            dbContext.OutboxMessages.Add(command.OutboxMessage);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SaveVendorInstallationResult(command.Installation, false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var message = await dbContext.OutboxMessages.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == command.RequestId, cancellationToken);
            if (message is null)
                throw;
            EnsureCompatible(message, command.OutboxMessage);
            return new SaveVendorInstallationResult(
                await GetExistingInstallationAsync(message.AccountId, cancellationToken), true);
        }
    }

    private async Task<Installation> GetExistingInstallationAsync(Guid accountId, CancellationToken cancellationToken) =>
        await dbContext.Installations.AsNoTracking().SingleAsync(item => item.AccountId == accountId, cancellationToken);

    private static void EnsureCompatible(OutboxMessage existing, OutboxMessage incoming)
    {
        if (existing.AccountId != incoming.AccountId ||
            existing.Payload.RootElement.GetProperty("appId").GetString() != incoming.Payload.RootElement.GetProperty("appId").GetString() ||
            existing.Payload.RootElement.GetProperty("cause").GetString() != incoming.Payload.RootElement.GetProperty("cause").GetString())
        {
            throw new VendorConflictException("Request ID is already associated with another callback.");
        }
    }
}
