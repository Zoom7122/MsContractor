using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface IMergeDocumentChangeService
{
    Task ChangeAsync(MergeJob job, MergeOperation operation, IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken);
}

public sealed class MergeDocumentChangeService(
    IDocumentSnapshotRepository documentSnapshots,
    IDocumentChangeEgressClient documentChangeClient,
    TimeProvider timeProvider) : IMergeDocumentChangeService
{
    private const int CommissionReportBatchSize = 950;

    public async Task ChangeAsync(MergeJob job, MergeOperation operation, IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken)
    {
        var rows = await documentSnapshots.GetForCounterpartiesAsync(job.AccountId, duplicateCounterpartyIds, cancellationToken);
        if (rows.Count == 0)
            return;

        var commissionRows = rows.Where(item => IsCommissionReport(item.DocumentType)).ToArray();
        if (commissionRows.Length > 0)
        {
            var commissionIds = commissionRows.Select(item => item.DocumentId).ToArray();
            var contracts = await documentSnapshots.GetCommissionsAsync(job.AccountId, commissionIds, cancellationToken);
            foreach (var chunk in commissionRows.Chunk(CommissionReportBatchSize))
            {
                var documents = chunk.Select(item => new MoySkladDocumentChangeAgentAndContractItem(item.DocumentType,
                    item.DocumentId, contracts.GetValueOrDefault(item.DocumentId))).ToArray();
                var response = await documentChangeClient.ChangeAgentAndContractAsync(job.AccountId, job.MainCounterpartyId,
                    documents, job.Id, operation.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
                var requested = documents.Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId)).ToArray();
                ValidateDocumentChangeResponse(job.MainCounterpartyId, requested, response);
                await ApplyChangedDocumentsAsync(chunk, response, job.MainCounterpartyId, cancellationToken);
                ThrowIfDocumentChangeFailed(response);
            }
        }

        var ordinaryRows = rows.Where(item => !IsCommissionReport(item.DocumentType)).ToArray();
        if (ordinaryRows.Length > 0)
        {
            var documents = ordinaryRows.Select(item => new MoySkladDocumentChangeItem(item.DocumentType, item.DocumentId)).ToArray();
            var response = await documentChangeClient.ChangeCounterpartyAsync(job.AccountId, job.MainCounterpartyId, documents,
                job.Id, operation.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
            ValidateDocumentChangeResponse(job.MainCounterpartyId, documents, response);
            await ApplyChangedDocumentsAsync(ordinaryRows, response, job.MainCounterpartyId, cancellationToken);
            ThrowIfDocumentChangeFailed(response);
        }
    }

    private async Task ApplyChangedDocumentsAsync(IReadOnlyList<CounterpartyDocument> rows,
        MoySkladDocumentChangeCounterpartyResponse response, Guid mainCounterpartyId, CancellationToken cancellationToken)
    {
        var changedKeys = response.ChangedDocuments.Select(item => (item.DocumentType, item.DocumentId)).ToHashSet();
        var now = timeProvider.GetUtcNow();
        foreach (var row in rows.Where(item => changedKeys.Contains((item.DocumentType, item.DocumentId))))
        {
            row.CounterpartyId = mainCounterpartyId;
            row.UpdatedAt = now;
        }
        await documentSnapshots.SaveProgressAsync(rows[0].AccountId, cancellationToken);
    }

    private static void ThrowIfDocumentChangeFailed(MoySkladDocumentChangeCounterpartyResponse response)
    {
        if (response.Failures.Count == 0) return;
        var allRetryable = response.Failures.All(item => item.Retryable);
        var representative = response.Failures.FirstOrDefault(item => !item.Retryable) ?? response.Failures[0];
        throw new MergeEgressException(representative.Code,
            $"Document counterparty change failed for {response.Failures.Count} document(s). " +
            $"document_type={representative.DocumentType}, document_id={representative.DocumentId:D}, " +
            $"endpoint={representative.Endpoint ?? "unknown"}, http_status={representative.StatusCode}, " +
            $"retryable={representative.Retryable}. {representative.Message}", allRetryable ? 503 : 400);
    }

    private static void ValidateDocumentChangeResponse(Guid mainCounterpartyId,
        IReadOnlyList<MoySkladDocumentChangeItem> requested, MoySkladDocumentChangeCounterpartyResponse response)
    {
        var requestedKeys = requested.Select(item => (item.DocumentType, item.DocumentId)).ToHashSet();
        var returnedKeys = response.ChangedDocuments.Select(item => (item.DocumentType, item.DocumentId))
            .Concat(response.SkippedDocuments.Select(item => (item.DocumentType, item.DocumentId)))
            .Concat(response.Failures.Select(item => (item.DocumentType, item.DocumentId))).ToArray();
        if (response.MainCounterpartyId != mainCounterpartyId || response.RequestedCount != requested.Count ||
            response.ChangedCount != response.ChangedDocuments.Count || response.SkippedCount != response.SkippedDocuments.Count ||
            response.FailedCount != response.Failures.Count || returnedKeys.Length != requested.Count ||
            returnedKeys.Distinct().Count() != returnedKeys.Length || !returnedKeys.ToHashSet().SetEquals(requestedKeys))
            throw new JsonException("Egress returned an inconsistent document change response.");
    }

    private static bool IsCommissionReport(string documentType) =>
        string.Equals(documentType, "commissionreportin", StringComparison.Ordinal) ||
        string.Equals(documentType, "commissionreportout", StringComparison.Ordinal);
}
