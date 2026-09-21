using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Persistence;

namespace MsContractor.MoySkladEgressService.Repositories;

public interface ISalesReturnRelationsRepository
{
    Task CreateSnapshotsAsync(
        Guid accountId,
        Guid operationId,
        IReadOnlyCollection<SalesReturnRelationsSnapshot> snapshots,
        CancellationToken cancellationToken);

    Task UpdateSourceStatusAsync(
        Guid accountId,
        Guid operationId,
        Guid sourceSalesReturnId,
        string status,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken);

    Task UpdateDocumentStatusAsync(
        Guid accountId,
        Guid operationId,
        Guid sourceSalesReturnId,
        SalesReturnRelationDocumentType documentType,
        Guid documentId,
        string detachStatus,
        string reattachStatus,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken);
}

public sealed class SalesReturnRelationsRepository : ISalesReturnRelationsRepository
{
    private readonly EgressDbContext _dbContext;

    public SalesReturnRelationsRepository(EgressDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task CreateSnapshotsAsync(
        Guid accountId,
        Guid operationId,
        IReadOnlyCollection<SalesReturnRelationsSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.SourceSalesReturnId == Guid.Empty)
                throw new ArgumentException("A snapshot source salesreturn id is required.", nameof(snapshots));

            var record = new SalesReturnRelationsSnapshotRecord
            {
                AccountId = accountId,
                OperationId = operationId,
                SourceSalesReturnId = snapshot.SourceSalesReturnId,
                Status = "Prepared"
            };

            record.PaymentOuts.AddRange(snapshot.PaymentOuts.Select(item => new PaymentOutRelationSnapshotRecord
            {
                AccountId = accountId,
                OperationId = operationId,
                SourceSalesReturnId = snapshot.SourceSalesReturnId,
                DocumentId = item.DocumentId,
                OperationsBeforeJson = JsonSerializer.Serialize(item.OperationsBefore),
                LinkedSum = item.LinkedSum
            }));
            record.CashOuts.AddRange(snapshot.CashOuts.Select(item => new CashOutRelationSnapshotRecord
            {
                AccountId = accountId,
                OperationId = operationId,
                SourceSalesReturnId = snapshot.SourceSalesReturnId,
                DocumentId = item.DocumentId,
                OperationsBeforeJson = JsonSerializer.Serialize(item.OperationsBefore),
                LinkedSum = item.LinkedSum
            }));
            record.Losses.AddRange(snapshot.Losses.Select(item => new LossRelationSnapshotRecord
            {
                AccountId = accountId,
                OperationId = operationId,
                SourceSalesReturnId = snapshot.SourceSalesReturnId,
                DocumentId = item.DocumentId,
                SalesReturnBeforeJson = item.SalesReturnBefore?.GetRawText()
            }));

            _dbContext.SalesReturnRelationsSnapshots.Add(record);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateSourceStatusAsync(
        Guid accountId,
        Guid operationId,
        Guid sourceSalesReturnId,
        string status,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken)
    {
        var record = await _dbContext.SalesReturnRelationsSnapshots.SingleAsync(
            item => item.AccountId == accountId &&
                    item.OperationId == operationId &&
                    item.SourceSalesReturnId == sourceSalesReturnId,
            cancellationToken);
        record.Status = status;
        record.ErrorCode = errorCode;
        record.Error = error;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateDocumentStatusAsync(
        Guid accountId,
        Guid operationId,
        Guid sourceSalesReturnId,
        SalesReturnRelationDocumentType documentType,
        Guid documentId,
        string detachStatus,
        string reattachStatus,
        string? errorCode,
        string? error,
        CancellationToken cancellationToken)
    {
        switch (documentType)
        {
            case SalesReturnRelationDocumentType.PaymentOut:
            {
                var record = await _dbContext.PaymentOutRelationSnapshots.SingleAsync(
                    item => item.AccountId == accountId && item.OperationId == operationId &&
                            item.SourceSalesReturnId == sourceSalesReturnId && item.DocumentId == documentId,
                    cancellationToken);
                record.DetachStatus = detachStatus;
                record.ReattachStatus = reattachStatus;
                record.ErrorCode = errorCode;
                record.Error = error;
                break;
            }
            case SalesReturnRelationDocumentType.CashOut:
            {
                var record = await _dbContext.CashOutRelationSnapshots.SingleAsync(
                    item => item.AccountId == accountId && item.OperationId == operationId &&
                            item.SourceSalesReturnId == sourceSalesReturnId && item.DocumentId == documentId,
                    cancellationToken);
                record.DetachStatus = detachStatus;
                record.ReattachStatus = reattachStatus;
                record.ErrorCode = errorCode;
                record.Error = error;
                break;
            }
            case SalesReturnRelationDocumentType.Loss:
            {
                var record = await _dbContext.LossRelationSnapshots.SingleAsync(
                    item => item.AccountId == accountId && item.OperationId == operationId &&
                            item.SourceSalesReturnId == sourceSalesReturnId && item.DocumentId == documentId,
                    cancellationToken);
                record.DetachStatus = detachStatus;
                record.ReattachStatus = reattachStatus;
                record.ErrorCode = errorCode;
                record.Error = error;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(documentType), documentType, null);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
