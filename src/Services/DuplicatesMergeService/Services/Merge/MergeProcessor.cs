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
    IFactureOutRecreationSender factureOutRecreationSender,
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
        await state.EnsureFactureOutRecreationOperationAsync(job, cancellationToken);
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

        var factureOutOperation = job.Operations.Single(item =>
            item.OperationType == MergeOperationTypes.RecreateFactureOuts);
        if (!await state.ExecuteAsync(job, factureOutOperation,
                token => RecreateFactureOutsAsync(
                    job,
                    factureOutOperation,
                    command.DuplicateCounterpartyIds,
                    factureOutRecreationSender,
                    documentSnapshots,
                    token),
                "Factureout recreation will be retried.", cancellationToken))
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

        var response = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "purchasereturn",
            purchaseReturns.Select(item => item.DocumentId).ToArray(),
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        ValidatePurchaseReturnResponse(purchaseReturns.Select(item => item.DocumentId).ToArray(), response);
        var returnIds = response.RecreatedPurchaseReturns.ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var invoiceIds = response.RecreatedFactureOuts.ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var changedRows = rows.Where(row =>
            row.DocumentType == "purchasereturn" && returnIds.ContainsKey(row.DocumentId) ||
            row.DocumentType == "factureout" && invoiceIds.ContainsKey(row.DocumentId)).ToArray();
        if (changedRows.Length > 0)
        {
            await documentSnapshots.ReplaceDocumentRowsAsync(job.AccountId,
                changedRows.Select(row => row.DocumentId).ToArray(),
                changedRows.Select(row => new CounterpartyDocument
                {
                    AccountId = job.AccountId,
                    CounterpartyId = job.MainCounterpartyId,
                    DocumentType = row.DocumentType,
                    DocumentId = row.DocumentType == "purchasereturn" ? returnIds[row.DocumentId] : invoiceIds[row.DocumentId],
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToArray(), cancellationToken);
        }

        if (!response.TransferredDocumentIds.ToHashSet().SetEquals(purchaseReturns.Select(item => item.DocumentId)))
        {
            var failed = response.CreatedWithErrors?.FirstOrDefault();
            var skipped = response.SkippedDocuments?.FirstOrDefault();
            throw new MergeEgressException(failed?.ErrorCode ?? skipped?.ErrorCode ?? "PURCHASERETURN_RECREATION_INCOMPLETE",
                failed?.Error ?? skipped?.Error ?? "Purchasereturn recreation skipped or failed one or more documents.", 400);
        }
    }

    private static void ValidatePurchaseReturnResponse(IReadOnlyList<Guid> requestedIds, PurchaseReturnRecreationResponse response)
    {
        static MergeEgressException Invalid() => new(MergeEgressException.PurchaseReturnUnknownResult,
            "MoySklad Egress Service returned an inconsistent purchasereturn recreation response; the remote result is unknown.", 502);
        if (response is null || response.TransferredDocumentIds is null || response.SkippedDocumentIds is null ||
            response.RecreatedPurchaseReturns is null || response.RecreatedFactureOuts is null)
            throw Invalid();
        var skipped = response.SkippedDocuments ?? [];
        var failed = response.CreatedWithErrors ?? [];
        var requested = requestedIds.ToHashSet();
        if (skipped.Any(item => item is null) || failed.Any(item => item is null) ||
            response.RecreatedPurchaseReturns.Any(item => item is null) || response.RecreatedFactureOuts.Any(item => item is null))
            throw Invalid();
        var returned = response.TransferredDocumentIds.Concat(response.SkippedDocumentIds)
            .Concat(failed.Select(item => item.SourceDocumentId)).ToArray();
        var createdIds = response.TransferredDocumentIds.Concat(failed.Select(item => item.SourceDocumentId)).ToHashSet();
        var mappings = response.RecreatedPurchaseReturns;
        var invoices = response.RecreatedFactureOuts;
        if (mappings.Select(item => item.SourceDocumentId).Distinct().Count() != mappings.Count)
            throw Invalid();
        var mappingBySource = mappings.ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        if (returned.Length != requested.Count || returned.Distinct().Count() != returned.Length ||
            !returned.ToHashSet().SetEquals(requested) ||
            skipped.Count != response.SkippedDocumentIds.Count ||
            skipped.Select(item => item.DocumentId).Distinct().Count() != skipped.Count ||
            !skipped.Select(item => item.DocumentId).ToHashSet().SetEquals(response.SkippedDocumentIds) ||
            skipped.Any(item => string.IsNullOrWhiteSpace(item.Status) || item.Status is "Verified" or "NeedsManualReview") ||
            failed.Any(item => item.NewDocumentId == Guid.Empty || string.IsNullOrWhiteSpace(item.Status) ||
                item.Status is "Verified" or "NeedsManualReview") ||
            mappings.Count != createdIds.Count ||
            !mappings.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(createdIds) ||
            mappings.Any(item => item.NewDocumentId == Guid.Empty || item.NewDocumentId == item.SourceDocumentId) ||
            failed.Any(item => !mappingBySource.TryGetValue(item.SourceDocumentId, out var newId) || newId != item.NewDocumentId) ||
            invoices.Select(item => item.SourceDocumentId).Distinct().Count() != invoices.Count ||
            invoices.Any(item => item.SourceDocumentId == Guid.Empty || item.NewDocumentId == Guid.Empty || item.NewDocumentId == item.SourceDocumentId ||
                requested.Contains(item.SourceDocumentId) || !createdIds.Contains(item.SourcePurchaseReturnId)) ||
            mappings.Select(item => item.NewDocumentId).Concat(invoices.Select(item => item.NewDocumentId)).Distinct().Count() !=
                mappings.Count + invoices.Count)
            throw Invalid();
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

    private static async Task RecreateFactureOutsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        IFactureOutRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, duplicateCounterpartyIds, cancellationToken);
        var factureOuts = rows
            .Where(item => string.Equals(item.DocumentType, "factureout", StringComparison.Ordinal))
            .ToArray();
        if (factureOuts.Length == 0)
            return;

        var sourceIds = factureOuts.Select(item => item.DocumentId).ToArray();
        var response = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "factureout",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        ValidateFactureOutResponse(sourceIds, response);

        if (response.TransferredDocumentIds.ToHashSet().SetEquals(sourceIds))
            return;

        var failed = response.FailedDocuments.FirstOrDefault();
        var createdWithError = response.CreatedWithErrors.FirstOrDefault();
        var skipped = response.SkippedDocuments.FirstOrDefault();
        throw new MergeEgressException(
            failed?.ErrorCode ?? createdWithError?.ErrorCode ?? skipped?.ErrorCode ??
            "FACTUREOUT_RECREATION_INCOMPLETE",
            failed?.Error ?? createdWithError?.Error ?? skipped?.Error ??
            "Factureout recreation skipped or failed one or more documents.",
            400);
    }

    private static void ValidateFactureOutResponse(
        IReadOnlyList<Guid> requestedIds,
        FactureOutRecreationResponse response)
    {
        if (response is null)
            throw InvalidFactureOutResponse();

        var transferred = response.TransferredDocumentIds;
        var skippedIds = response.SkippedDocumentIds;
        var skippedDocuments = response.SkippedDocuments;
        var createdWithErrors = response.CreatedWithErrors;
        var failedDocuments = response.FailedDocuments;
        if (transferred is null || skippedIds is null || skippedDocuments is null ||
            createdWithErrors is null || failedDocuments is null)
        {
            throw InvalidFactureOutResponse();
        }

        var returnedIds = transferred
            .Concat(skippedIds)
            .Concat(createdWithErrors.Select(item => item.SourceDocumentId))
            .Concat(failedDocuments.Select(item => item.SourceDocumentId))
            .ToArray();
        var requested = requestedIds.ToHashSet();
        var skippedDetailIds = skippedDocuments.Select(item => item.DocumentId).ToArray();
        if (returnedIds.Length != requested.Count ||
            returnedIds.Distinct().Count() != returnedIds.Length ||
            !returnedIds.ToHashSet().SetEquals(requested) ||
            skippedDetailIds.Length != skippedIds.Count ||
            skippedDetailIds.Distinct().Count() != skippedDetailIds.Length ||
            !skippedDetailIds.ToHashSet().SetEquals(skippedIds) ||
            skippedDocuments.Any(item => item.Status != "Skipped") ||
            createdWithErrors.Any(item => item.Status != "CreatedWithErrors") ||
            failedDocuments.Any(item => item.Status != "Failed"))
        {
            throw InvalidFactureOutResponse();
        }
    }

    private static MergeEgressException InvalidFactureOutResponse() => new(
        "EGRESS_INVALID_RESPONSE",
        "MoySklad Egress Service returned an inconsistent factureout recreation response.",
        502);
}
