using MsContractor.VendorService.Models;
using MsContractor.VendorService.Models.Exceptions;
using MsContractor.VendorService.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MsContractor.VendorService.Repositories;

public interface IVendorInstallationRepository
{
    Task<Installation?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken);

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<SaveVendorInstallationResult> SaveAsync(
        SaveVendorInstallationCommand command,
        CancellationToken cancellationToken);
}

public sealed class VendorInstallationRepository : IVendorInstallationRepository
{
    private readonly VendorDbContext _dbContext;

    public VendorInstallationRepository(
        VendorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Installation?> GetByAccountIdAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await _dbContext.SetTenantAsync(accountId, cancellationToken);
        return await _dbContext.Installations.SingleOrDefaultAsync(
            item => item.AccountId == accountId,
            cancellationToken);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _dbContext.SaveChangesAsync(cancellationToken);

    public async Task<SaveVendorInstallationResult> SaveAsync(
        SaveVendorInstallationCommand command,
        CancellationToken cancellationToken)
    {
        var accountId = command.Installation?.AccountId ?? command.OutboxMessage?.AccountId;
        if (accountId is not null)
            await _dbContext.SetTenantAsync(accountId.Value, cancellationToken);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (command.OutboxMessage is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new SaveVendorInstallationResult(command.Installation, false);
        }

        var priorMessage = await _dbContext.OutboxMessages.AsNoTracking()
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
            if (command.Installation is not null && _dbContext.Entry(command.Installation).State == EntityState.Detached)
                _dbContext.Installations.Add(command.Installation);
            _dbContext.OutboxMessages.Add(command.OutboxMessage);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new SaveVendorInstallationResult(command.Installation, false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            var message = await _dbContext.OutboxMessages.AsNoTracking()
                .SingleOrDefaultAsync(item => item.RequestId == command.RequestId, cancellationToken);
            if (message is null)
                throw;
            EnsureCompatible(message, command.OutboxMessage);
            return new SaveVendorInstallationResult(
                await GetExistingInstallationAsync(message.AccountId, cancellationToken), true);
        }
    }

    private async Task<Installation> GetExistingInstallationAsync(Guid accountId, CancellationToken cancellationToken) =>
        await _dbContext.Installations.AsNoTracking().SingleAsync(item => item.AccountId == accountId, cancellationToken);

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
