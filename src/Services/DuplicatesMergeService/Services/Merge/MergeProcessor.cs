using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;
using MsContractor.Contracts.Merge;
using MsContractor.DuplicatesMergeService.Services.Merge.Counterparties;
using MsContractor.DuplicatesMergeService.Services.Merge.Documents;

namespace MsContractor.DuplicatesMergeService.Services.Merge;

public interface IMergeProcessor
{
    Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken);
}

public sealed class MergeProcessor(
    IMergeCommandValidator commandValidator,
    IMergeOperationStateService state,
    IMergeDocumentDiscoveryService documentDiscovery,
    IMergeMainCounterpartyUpdateService mainCounterpartyUpdate,
    IMergeDocumentChangeService documentChange,
    ISalesReturnRecreationSender salesReturnRecreationSender,
    IPurchaseReturnRecreationSender purchaseReturnRecreationSender,
    IFactureInRecreationSender factureInRecreationSender,
    IDocumentSnapshotRepository documentSnapshots,
    IMergeCounterpartyArchiveService counterpartyArchive,
    ILogger<MergeProcessor> logger) : IMergeProcessor
{
    public async Task ProcessAsync(MergeRequested command, CancellationToken cancellationToken)
    {
        var context = await commandValidator.ValidateAsync(command, cancellationToken);
        if (context is null) return;

        var job = context.Job;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["account_id"] = job.AccountId,
            ["merge_job_id"] = job.Id,
            ["message_id"] = job.MessageId,
            ["correlation_id"] = job.CorrelationId
        });

        await state.EnsureDocumentChangeOperationAsync(job, cancellationToken);
        await state.EnsureSalesReturnRecreationOperationAsync(job, cancellationToken);
        await state.EnsurePurchaseReturnRecreationOperationAsync(job, cancellationToken);
        await state.EnsureFactureInRecreationOperationAsync(job, cancellationToken);
        await state.StartJobAsync(job, cancellationToken);

        var discovery = job.Operations.Single(item => item.OperationType == MergeOperationTypes.DiscoverDocuments);
        if (!await state.ExecuteAsync(job, discovery,
                token => documentDiscovery.DiscoverAsync(job, discovery, command.DuplicateCounterpartyIds, token),
                "Document discovery will be retried.", cancellationToken))
            return;

        var documentsToTransferCount = (await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, command.DuplicateCounterpartyIds, cancellationToken)).Count;

        var update = job.Operations.Single(item => item.OperationType == MergeOperationTypes.UpdateMainCounterparty);
        if (!await state.ExecuteAsync(job, update,
                token => mainCounterpartyUpdate.UpdateAsync(job, update, context.MainCounterparty, token),
                "Update main counterparty will be retried.", cancellationToken))
            return;

        var documentChangeOperation = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.ChangeDocumentCounterparties);
        if (!await state.ExecuteAsync(job, documentChangeOperation,
                token => documentChange.ChangeAsync(job, documentChangeOperation, command.DuplicateCounterpartyIds, token),
                "Document counterparty change will be retried.", cancellationToken))
            return;

        var salesReturnOperation = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.RecreateSalesReturns);
        if (!await state.ExecuteAsync(job, salesReturnOperation,
                token => RecreateSalesReturnsAsync(
                    job,
                    salesReturnOperation,
                    command.DuplicateCounterpartyIds,
                    salesReturnRecreationSender,
                    documentSnapshots,
                    token),
                "Salesreturn recreation will be retried.", cancellationToken))
            return;

        var purchaseReturnOperation = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.RecreatePurchaseReturns);
        if (!await state.ExecuteAsync(job, purchaseReturnOperation,
                token => RecreatePurchaseReturnsAsync(
                    job,
                    purchaseReturnOperation,
                    command.DuplicateCounterpartyIds,
                    purchaseReturnRecreationSender,
                    documentSnapshots,
                    token),
                "Purchasereturn recreation will be retried.", cancellationToken))
            return;

        var factureInOperation = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.RecreateFactureIns);
        if (!await state.ExecuteAsync(job, factureInOperation,
                token => RecreateFactureInsAsync(
                    job,
                    factureInOperation,
                    command.DuplicateCounterpartyIds,
                    factureInRecreationSender,
                    documentSnapshots,
                    token),
                "Facturein recreation will be retried.", cancellationToken))
            return;

        var archiveOperations = job.Operations
            .Where(item => item.OperationType == MergeOperationTypes.ArchiveDuplicate)
            .Where(item => !MergeOperationStatuses.IsTerminal(item.Status))
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (archiveOperations.Length > 0)
        {
            await state.ExecuteBatchAsync(job, archiveOperations,
                token => counterpartyArchive.ArchiveAsync(job, archiveOperations, token),
                "Archive counterparties will be retried.", cancellationToken);
        }

        await state.CompleteJobAsync(job, cancellationToken);
        logger.LogInformation(
            "Merge completed: merge_job_id={MergeJobId}, account_id={AccountId}, main_counterparty_id={MainCounterpartyId}, duplicate_counterparties_count={DuplicateCounterpartiesCount}, documents_transferred_count={DocumentsTransferredCount}, status={Status}",
            job.Id,
            job.AccountId,
            job.MainCounterpartyId,
            command.DuplicateCounterpartyIds.Count,
            documentsToTransferCount,
            job.Status);
    }

    private static async Task RecreateSalesReturnsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        ISalesReturnRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, duplicateCounterpartyIds, cancellationToken);
        var salesReturns = rows
            .Where(item => string.Equals(item.DocumentType, "salesreturn", StringComparison.Ordinal))
            .ToArray();
        if (salesReturns.Length == 0)
            return;

        var sourceIds = salesReturns.Select(item => item.DocumentId).ToArray();
        var response = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "salesreturn",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        ValidateSalesReturnResponse(job.MainCounterpartyId, sourceIds, response);

        var responseBySourceId = response.Documents.ToDictionary(item => item.SourceDocumentId);
        var now = DateTimeOffset.UtcNow;
        var replacements = salesReturns.Select(row =>
        {
            var item = responseBySourceId[row.DocumentId];
            return item.Status == "Completed" && item.NewDocumentId is Guid newDocumentId
                ? new CounterpartyDocument
                {
                    AccountId = row.AccountId,
                    CounterpartyId = job.MainCounterpartyId,
                    DocumentType = row.DocumentType,
                    DocumentId = newDocumentId,
                    UpdatedAt = now
                }
                : row;
        }).ToArray();

        await documentSnapshots.ReplaceDocumentRowsAsync(
            job.AccountId,
            salesReturns.Select(row => row.DocumentId).ToArray(),
            replacements,
            cancellationToken);

        var failed = response.Documents.FirstOrDefault(item =>
            item.Status != "Completed" || item.NewDocumentId is null);
        if (failed is not null)
        {
            throw new MergeEgressException(
                failed.ErrorCode ?? "SALESRETURN_RECREATION_FAILED",
                failed.Error ?? $"Salesreturn recreation failed for document {failed.SourceDocumentId:D}.",
                400);
        }
    }

    private static void ValidateSalesReturnResponse(
        Guid mainAgentId,
        IReadOnlyList<Guid> requestedIds,
        SalesReturnRecreationResponse response)
    {
        var requested = requestedIds.ToHashSet();
        var returned = response.Documents.Select(item => item.SourceDocumentId).ToArray();
        if (response.MainAgentId != mainAgentId ||
            returned.Length != requested.Count ||
            returned.Distinct().Count() != returned.Length ||
            !returned.ToHashSet().SetEquals(requested))
        {
            throw new MergeEgressException(
                "EGRESS_INVALID_RESPONSE",
                "MoySklad Egress Service returned an inconsistent salesreturn recreation response.",
                502);
        }
    }

    private static async Task RecreatePurchaseReturnsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        IPurchaseReturnRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, duplicateCounterpartyIds, cancellationToken);
        var purchaseReturns = rows
            .Where(item => string.Equals(item.DocumentType, "purchasereturn", StringComparison.Ordinal))
            .ToArray();
        if (purchaseReturns.Length == 0)
            return;

        await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "purchasereturn",
            purchaseReturns.Select(item => item.DocumentId).ToArray(),
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);
    }

    private static async Task RecreateFactureInsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        IFactureInRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, duplicateCounterpartyIds, cancellationToken);
        var factureIns = rows
            .Where(item => string.Equals(item.DocumentType, "facturein", StringComparison.Ordinal))
            .ToArray();
        if (factureIns.Length == 0)
            return;

        var sourceIds = factureIns.Select(item => item.DocumentId).ToArray();
        var response = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "facturein",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        var skipped = response.SkippedDocumentIds.ToHashSet();
        var failed = response.FailedDocuments?.Select(item => item.SourceDocumentId).ToHashSet() ?? [];
        var createdWithErrors = response.CreatedWithErrors?.Select(item => item.SourceDocumentId).ToHashSet() ?? [];
        var transferred = response.TransferredDocumentIds.ToHashSet();
        if (!transferred.SetEquals(sourceIds) || skipped.Count != 0 || failed.Count != 0 || createdWithErrors.Count != 0)
        {
            var failedItem = response.FailedDocuments?.FirstOrDefault();
            var createdWithError = response.CreatedWithErrors?.FirstOrDefault();
            throw new MergeEgressException(
                failedItem?.ErrorCode ?? createdWithError?.ErrorCode ?? "FACTUREIN_RECREATION_INCOMPLETE",
                failedItem?.Error ?? createdWithError?.Error ?? "Facturein recreation skipped or failed one or more documents.",
                400);
        }
    }
}
