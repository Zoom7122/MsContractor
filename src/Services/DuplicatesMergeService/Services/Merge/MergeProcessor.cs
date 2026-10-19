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
    IDocumentsInOperationRepository documentsInOperation,
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
                    documentsInOperation,
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
                    documentsInOperation,
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
                    documentsInOperation,
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
                    documentsInOperation,
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
        IDocumentsInOperationRepository documentsInOperation,
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
        var egressResult = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "salesreturn",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);
        await documentsInOperation.AppendEgressResponseAsync(
            job.AccountId,
            job.Id,
            DocumentsInOperationTypes.RecreateSalesReturn,
            egressResult.StatusCode,
            egressResult.Body,
            cancellationToken);

        var response = MergeEgressResponseReader.Read<SalesReturnRecreationResponse>(
            egressResult,
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an empty salesreturn recreation response.",
            "MoySklad Egress Service returned an invalid salesreturn recreation response.");
        ValidateSalesReturnResponse(sourceIds, response);

        var transferredBySourceId = response.TransferredDocuments!
            .ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var createdWithErrorsBySourceId = response.CreatedWithErrors!
            .ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var unCreatedButDeletedIds = response.UnCreatedButDeleted!
            .Select(item => item.SourceDocumentId)
            .ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var replacements = salesReturns
            .Where(row => !unCreatedButDeletedIds.Contains(row.DocumentId))
            .Select(row =>
        {
            var hasReplacement = transferredBySourceId.TryGetValue(row.DocumentId, out var newDocumentId) ||
                                 createdWithErrorsBySourceId.TryGetValue(row.DocumentId, out newDocumentId);
            return hasReplacement
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

        var createdWithError = response.CreatedWithErrors!.FirstOrDefault();
        var unCreatedButDeleted = response.UnCreatedButDeleted!.FirstOrDefault();
        if (response.CreatedWithErrors!.Count + response.UnCreatedButDeleted!.Count > 0)
        {
            throw new MergeEgressException(
                unCreatedButDeleted?.ErrorCode ?? createdWithError?.ErrorCode ?? "SALESRETURN_RECREATION_INCOMPLETE",
                unCreatedButDeleted?.Error ?? createdWithError?.Error ??
                "Salesreturn recreation left a deleted source document without a successful replacement or returned a created document with errors.",
                400);
        }
    }

    private static void ValidateSalesReturnResponse(
        IReadOnlyList<Guid> requestedIds,
        SalesReturnRecreationResponse response)
    {
        static MergeEgressException Invalid() => new(
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an inconsistent salesreturn recreation response.",
            502);

        if (response is null || response.TransferredDocumentIds is null ||
            response.TransferredDocuments is null || response.SkippedDocumentIds is null ||
            response.SkippedDocuments is null || response.CreatedWithErrors is null ||
            response.UnCreatedButDeleted is null)
            throw Invalid();

        if (response.TransferredDocuments.Any(item => item is null) ||
            response.SkippedDocuments.Any(item => item is null || item.Status is not ("Skipped" or "Failed")) ||
            response.CreatedWithErrors.Any(item => item is null) ||
            response.UnCreatedButDeleted.Any(item => item is null))
            throw Invalid();

        var requested = requestedIds.ToHashSet();
        var transferred = response.TransferredDocumentIds.ToArray();
        var transferredMappings = response.TransferredDocuments.ToArray();
        var skippedIds = response.SkippedDocumentIds.ToArray();
        var skippedDetails = response.SkippedDocuments.Select(item => item.DocumentId).ToArray();
        var createdWithErrors = response.CreatedWithErrors.ToArray();
        var unCreatedButDeleted = response.UnCreatedButDeleted.ToArray();
        var outcomeSourceIds = transferred
            .Concat(skippedIds)
            .Concat(createdWithErrors.Select(item => item.SourceDocumentId))
            .Concat(unCreatedButDeleted.Select(item => item.SourceDocumentId))
            .ToArray();
        var replacementIds = transferredMappings.Select(item => item.NewDocumentId)
            .Concat(createdWithErrors.Select(item => item.NewDocumentId))
            .ToArray();

        if (transferred.Any(id => id == Guid.Empty) || skippedIds.Any(id => id == Guid.Empty) ||
            outcomeSourceIds.Length != requested.Count || outcomeSourceIds.Distinct().Count() != outcomeSourceIds.Length ||
            !outcomeSourceIds.ToHashSet().SetEquals(requested) ||
            skippedDetails.Length != skippedIds.Length || skippedDetails.Distinct().Count() != skippedDetails.Length ||
            !skippedDetails.ToHashSet().SetEquals(skippedIds) ||
            transferredMappings.Length != transferred.Length ||
            transferredMappings.Select(item => item.SourceDocumentId).Distinct().Count() != transferredMappings.Length ||
            !transferredMappings.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(transferred) ||
            createdWithErrors.Any(item => item.Status != "CreatedWithError" || item.NewDocumentId == Guid.Empty) ||
            unCreatedButDeleted.Any(item => item.Status != "Failed") ||
            replacementIds.Any(id => id == Guid.Empty) || replacementIds.Distinct().Count() != replacementIds.Length)
        {
            throw Invalid();
        }
    }

    private static async Task RecreatePurchaseReturnsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        IPurchaseReturnRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        IDocumentsInOperationRepository documentsInOperation,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(
            job.AccountId, duplicateCounterpartyIds, cancellationToken);
        var purchaseReturns = rows
            .Where(item => string.Equals(item.DocumentType, "purchasereturn", StringComparison.Ordinal))
            .ToArray();
        if (purchaseReturns.Length == 0)
            return;

        var egressResponse = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "purchasereturn",
            purchaseReturns.Select(item => item.DocumentId).ToArray(),
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        await documentsInOperation.AppendEgressResponseAsync(
            job.AccountId,
            job.Id,
            DocumentsInOperationTypes.RecreatePurchaseReturn,
            egressResponse.StatusCode,
            egressResponse.Body,
            cancellationToken);
        var response = MergeEgressResponseReader.Read<PurchaseReturnRecreationResponse>(
            egressResponse,
            MergeEgressException.PurchaseReturnUnknownResult,
            "MoySklad Egress Service returned an empty purchasereturn recreation response.",
            "MoySklad Egress Service returned an invalid purchasereturn recreation response.",
            unknownResultOnServerError: true);

        ValidatePurchaseReturnResponse(purchaseReturns.Select(item => item.DocumentId).ToArray(), response);
        var returnIds = response.RecreatedPurchaseReturns.ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var invoiceIds = response.RecreatedFactureOuts.ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var unCreatedButDeletedIds = response.UnCreatedButDeleted!
            .Select(item => item.SourceDocumentId)
            .ToHashSet();
        var changedRows = rows.Where(row =>
            row.DocumentType == "purchasereturn" && returnIds.ContainsKey(row.DocumentId) ||
            row.DocumentType == "factureout" && invoiceIds.ContainsKey(row.DocumentId)).ToArray();
        var removedRows = rows.Where(row =>
            row.DocumentType == "purchasereturn" && unCreatedButDeletedIds.Contains(row.DocumentId)).ToArray();
        if (changedRows.Length > 0 || removedRows.Length > 0)
        {
            await documentSnapshots.ReplaceDocumentRowsAsync(job.AccountId,
                changedRows.Select(row => row.DocumentId)
                    .Concat(removedRows.Select(row => row.DocumentId))
                    .Distinct()
                    .ToArray(),
                changedRows.Select(row => new CounterpartyDocument
                {
                    AccountId = job.AccountId,
                    CounterpartyId = job.MainCounterpartyId,
                    DocumentType = row.DocumentType,
                    DocumentId = row.DocumentType == "purchasereturn" ? returnIds[row.DocumentId] : invoiceIds[row.DocumentId],
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToArray(), cancellationToken);
        }

        var createdWithErrors = response.CreatedWithErrors ?? [];
        var unCreatedButDeleted = response.UnCreatedButDeleted ?? [];
        if (createdWithErrors.Count + unCreatedButDeleted.Count > 0)
        {
            var failed = createdWithErrors.FirstOrDefault();
            var unCreated = unCreatedButDeleted.FirstOrDefault();
            throw new MergeEgressException(unCreated?.ErrorCode ?? failed?.ErrorCode ?? "PURCHASERETURN_RECREATION_INCOMPLETE",
                unCreated?.Error ?? failed?.Error ??
                "Purchasereturn recreation left a deleted source document without a successful replacement or returned a created document with errors.", 400);
        }
    }

    private static void ValidatePurchaseReturnResponse(IReadOnlyList<Guid> requestedIds, PurchaseReturnRecreationResponse response)
    {
        static MergeEgressException Invalid() => new(MergeEgressException.PurchaseReturnUnknownResult,
            "MoySklad Egress Service returned an inconsistent purchasereturn recreation response; the remote result is unknown.", 502);
        if (response is null || response.TransferredDocumentIds is null || response.TransferredDocuments is null ||
            response.SkippedDocumentIds is null || response.UnCreatedButDeleted is null ||
            response.RecreatedPurchaseReturns is null || response.RecreatedFactureOuts is null)
            throw Invalid();
        var skipped = response.SkippedDocuments ?? [];
        var failed = response.CreatedWithErrors ?? [];
        var unCreated = response.UnCreatedButDeleted;
        var requested = requestedIds.ToHashSet();
        if (skipped.Any(item => item is null) || failed.Any(item => item is null) ||
            unCreated.Any(item => item is null) || response.TransferredDocuments.Any(item => item is null) ||
            response.RecreatedPurchaseReturns.Any(item => item is null) || response.RecreatedFactureOuts.Any(item => item is null))
            throw Invalid();
        var returned = response.TransferredDocumentIds.Concat(response.SkippedDocumentIds)
            .Concat(failed.Select(item => item.SourceDocumentId))
            .Concat(unCreated.Select(item => item.SourceDocumentId)).ToArray();
        var createdIds = response.TransferredDocumentIds.Concat(failed.Select(item => item.SourceDocumentId)).ToHashSet();
        var mappings = response.RecreatedPurchaseReturns;
        var invoices = response.RecreatedFactureOuts;
        var transferredMappings = response.TransferredDocuments;
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
            unCreated.Any(item => item.Status != "Failed") ||
            transferredMappings.Count != response.TransferredDocumentIds.Count ||
            transferredMappings.Select(item => item.SourceDocumentId).Distinct().Count() != transferredMappings.Count ||
            !transferredMappings.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(response.TransferredDocumentIds) ||
            transferredMappings.Any(item => item.NewDocumentId == Guid.Empty || item.NewDocumentId == item.SourceDocumentId) ||
            mappings.Count != createdIds.Count ||
            !mappings.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(createdIds) ||
            mappings.Any(item => item.NewDocumentId == Guid.Empty || item.NewDocumentId == item.SourceDocumentId) ||
            failed.Any(item => !mappingBySource.TryGetValue(item.SourceDocumentId, out var newId) || newId != item.NewDocumentId) ||
            transferredMappings.Any(item => !mappingBySource.TryGetValue(item.SourceDocumentId, out var newId) || newId != item.NewDocumentId) ||
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
        IDocumentsInOperationRepository documentsInOperation,
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
        var egressResponse = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "facturein",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        await documentsInOperation.AppendEgressResponseAsync(
            job.AccountId,
            job.Id,
            DocumentsInOperationTypes.RecreateFactureIn,
            egressResponse.StatusCode,
            egressResponse.Body,
            cancellationToken);
        var response = MergeEgressResponseReader.Read<FactureInRecreationResponse>(
            egressResponse,
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an empty facturein recreation response.",
            "MoySklad Egress Service returned an invalid facturein recreation response.");

        ValidateFactureInResponse(sourceIds, response);

        var createdWithErrors = response.CreatedWithErrors ?? [];
        var unCreatedButDeleted = response.UnCreatedButDeleted!;
        var replacements = response.TransferredDocuments!
            .Select(item => (item.SourceDocumentId, item.NewDocumentId))
            .Concat(createdWithErrors.Select(item => (item.SourceDocumentId, item.NewDocumentId)))
            .ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var changedRows = rows.Where(item =>
            item.DocumentType == "facturein" && replacements.ContainsKey(item.DocumentId)).ToArray();
        var removedRows = rows.Where(item =>
            item.DocumentType == "facturein" && unCreatedButDeleted.Any(outcome => outcome.SourceDocumentId == item.DocumentId))
            .ToArray();
        if (changedRows.Length > 0 || removedRows.Length > 0)
        {
            await documentSnapshots.ReplaceDocumentRowsAsync(
                job.AccountId,
                changedRows.Select(item => item.DocumentId)
                    .Concat(removedRows.Select(item => item.DocumentId))
                    .Distinct()
                    .ToArray(),
                changedRows.Select(item => new CounterpartyDocument
                {
                    AccountId = item.AccountId,
                    CounterpartyId = job.MainCounterpartyId,
                    DocumentType = item.DocumentType,
                    DocumentId = replacements[item.DocumentId],
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToArray(),
                cancellationToken);
        }

        if (createdWithErrors.Count + unCreatedButDeleted.Count > 0)
        {
            var unCreated = unCreatedButDeleted.FirstOrDefault();
            var createdWithError = createdWithErrors.FirstOrDefault();
            throw new MergeEgressException(
                unCreated?.ErrorCode ?? createdWithError?.ErrorCode ?? "FACTUREIN_RECREATION_INCOMPLETE",
                unCreated?.Error ?? createdWithError?.Error ??
                "Facturein recreation left a deleted source document without a successful replacement or returned a created document with errors.",
                400);
        }
    }

    private static void ValidateFactureInResponse(
        IReadOnlyList<Guid> requestedIds,
        FactureInRecreationResponse response)
    {
        static MergeEgressException Invalid() => new(
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an inconsistent facturein recreation response.",
            502);

        if (response is null || response.TransferredDocumentIds is null || response.TransferredDocuments is null ||
            response.SkippedDocumentIds is null || response.UnCreatedButDeleted is null)
            throw Invalid();

        var transferred = response.TransferredDocumentIds;
        var skipped = response.SkippedDocumentIds;
        var transferredDetails = response.TransferredDocuments;
        var skippedDetails = response.SkippedDocuments ?? [];
        var createdWithErrors = response.CreatedWithErrors ?? [];
        var unCreatedButDeleted = response.UnCreatedButDeleted;
        if (transferredDetails.Any(item => item is null) || skippedDetails.Any(item => item is null) ||
            createdWithErrors.Any(item => item is null) || unCreatedButDeleted.Any(item => item is null))
            throw Invalid();

        var requested = requestedIds.ToHashSet();
        var outcomeIds = transferred.Concat(skipped)
            .Concat(createdWithErrors.Select(item => item.SourceDocumentId))
            .Concat(unCreatedButDeleted.Select(item => item.SourceDocumentId))
            .ToArray();
        var skippedDetailIds = skippedDetails.Select(item => item.DocumentId).ToArray();
        if (outcomeIds.Length != requested.Count || outcomeIds.Distinct().Count() != outcomeIds.Length ||
            !outcomeIds.ToHashSet().SetEquals(requested) ||
            transferred.Any(item => item == Guid.Empty) || skipped.Any(item => item == Guid.Empty) ||
            skippedDetailIds.Length != skipped.Count || skippedDetailIds.Distinct().Count() != skippedDetailIds.Length ||
            !skippedDetailIds.ToHashSet().SetEquals(skipped) ||
            transferredDetails.Count != transferred.Count ||
            transferredDetails.Select(item => item.SourceDocumentId).Distinct().Count() != transferredDetails.Count ||
            !transferredDetails.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(transferred) ||
            transferredDetails.Any(item => item.SourceDocumentId == Guid.Empty || item.NewDocumentId == Guid.Empty ||
                                           item.NewDocumentId == item.SourceDocumentId) ||
            createdWithErrors.Any(item => item.NewDocumentId == Guid.Empty || item.Status != "CreatedWithError") ||
            unCreatedButDeleted.Any(item => item.Status != "Failed"))
            throw Invalid();
    }

    private static async Task RecreateFactureOutsAsync(
        MergeJob job,
        MergeOperation operation,
        IReadOnlyList<Guid> duplicateCounterpartyIds,
        IFactureOutRecreationSender sender,
        IDocumentSnapshotRepository documentSnapshots,
        IDocumentsInOperationRepository documentsInOperation,
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
        var egressResponse = await sender.SendAsync(
            job.AccountId,
            job.MainCounterpartyId,
            "factureout",
            sourceIds,
            job.Id,
            operation.Id,
            job.RequestedByUserId,
            job.CorrelationId,
            cancellationToken);

        await documentsInOperation.AppendEgressResponseAsync(
            job.AccountId,
            job.Id,
            DocumentsInOperationTypes.RecreateFactureOut,
            egressResponse.StatusCode,
            egressResponse.Body,
            cancellationToken);
        var response = MergeEgressResponseReader.Read<FactureOutRecreationResponse>(
            egressResponse,
            "EGRESS_INVALID_RESPONSE",
            "MoySklad Egress Service returned an empty factureout recreation response.",
            "MoySklad Egress Service returned an invalid factureout recreation response.");

        ValidateFactureOutResponse(sourceIds, response);

        var createdWithErrors = response.CreatedWithErrors;
        var unCreatedButDeleted = response.UnCreatedButDeleted;
        var replacements = response.TransferredDocuments
            .Select(item => (item.SourceDocumentId, item.NewDocumentId))
            .Concat(createdWithErrors.Select(item => (item.SourceDocumentId, item.NewDocumentId)))
            .ToDictionary(item => item.SourceDocumentId, item => item.NewDocumentId);
        var changedRows = rows.Where(item =>
            item.DocumentType == "factureout" && replacements.ContainsKey(item.DocumentId)).ToArray();
        var removedRows = rows.Where(item =>
            item.DocumentType == "factureout" && unCreatedButDeleted.Any(outcome => outcome.SourceDocumentId == item.DocumentId))
            .ToArray();
        if (changedRows.Length > 0 || removedRows.Length > 0)
        {
            await documentSnapshots.ReplaceDocumentRowsAsync(
                job.AccountId,
                changedRows.Select(item => item.DocumentId)
                    .Concat(removedRows.Select(item => item.DocumentId))
                    .Distinct()
                    .ToArray(),
                changedRows.Select(item => new CounterpartyDocument
                {
                    AccountId = item.AccountId,
                    CounterpartyId = job.MainCounterpartyId,
                    DocumentType = item.DocumentType,
                    DocumentId = replacements[item.DocumentId],
                    UpdatedAt = DateTimeOffset.UtcNow
                }).ToArray(),
                cancellationToken);
        }

        if (createdWithErrors.Count + unCreatedButDeleted.Count > 0)
        {
            var unCreated = unCreatedButDeleted.FirstOrDefault();
            var createdWithError = createdWithErrors.FirstOrDefault();
            throw new MergeEgressException(
                unCreated?.ErrorCode ?? createdWithError?.ErrorCode ?? "FACTUREOUT_RECREATION_INCOMPLETE",
                unCreated?.Error ?? createdWithError?.Error ??
                "Factureout recreation left a deleted source document without a successful replacement or returned a created document with errors.",
                400);
        }
    }

    private static void ValidateFactureOutResponse(
        IReadOnlyList<Guid> requestedIds,
        FactureOutRecreationResponse response)
    {
        if (response is null)
            throw InvalidFactureOutResponse();

        var transferred = response.TransferredDocumentIds;
        var transferredDocuments = response.TransferredDocuments;
        var skippedIds = response.SkippedDocumentIds;
        var skippedDocuments = response.SkippedDocuments;
        var createdWithErrors = response.CreatedWithErrors;
        var unCreatedButDeleted = response.UnCreatedButDeleted;
        if (transferred is null || transferredDocuments is null || skippedIds is null || skippedDocuments is null ||
            createdWithErrors is null || unCreatedButDeleted is null)
        {
            throw InvalidFactureOutResponse();
        }

        if (transferredDocuments.Any(item => item is null) ||
            skippedDocuments.Any(item => item is null) ||
            createdWithErrors.Any(item => item is null) ||
            unCreatedButDeleted.Any(item => item is null))
        {
            throw InvalidFactureOutResponse();
        }

        var returnedIds = transferred
            .Concat(skippedIds)
            .Concat(createdWithErrors.Select(item => item.SourceDocumentId))
            .Concat(unCreatedButDeleted.Select(item => item.SourceDocumentId))
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
            transferredDocuments.Count != transferred.Count ||
            transferredDocuments.Select(item => item.SourceDocumentId).Distinct().Count() != transferredDocuments.Count ||
            !transferredDocuments.Select(item => item.SourceDocumentId).ToHashSet().SetEquals(transferred) ||
            transferredDocuments.Any(item => item.SourceDocumentId == Guid.Empty || item.NewDocumentId == Guid.Empty ||
                                              item.NewDocumentId == item.SourceDocumentId) ||
            createdWithErrors.Any(item => item.Status != "CreatedWithError" || item.NewDocumentId == Guid.Empty) ||
            unCreatedButDeleted.Any(item => item.Status != "Failed"))
        {
            throw InvalidFactureOutResponse();
        }
    }

    private static MergeEgressException InvalidFactureOutResponse() => new(
        "EGRESS_INVALID_RESPONSE",
        "MoySklad Egress Service returned an inconsistent factureout recreation response.",
        502);
}
