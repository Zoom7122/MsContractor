using System.Text.Json;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Clients;
using MsContractor.DuplicatesMergeService.Repositories;

namespace MsContractor.DuplicatesMergeService.Services.Merge.Documents;

public interface IMergeDocumentDiscoveryService
{
    Task DiscoverAsync(MergeJob job, MergeOperation operation, IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken);
}

public sealed class MergeDocumentDiscoveryService(
    IDocumentDiscoveryEgressClient documentDiscoveryClient,
    IDocumentSnapshotRepository documentSnapshots,
    TimeProvider timeProvider,
    ILogger<MergeDocumentDiscoveryService> logger) : IMergeDocumentDiscoveryService
{
    public async Task DiscoverAsync(MergeJob job, MergeOperation operation, IReadOnlyList<Guid> duplicateCounterpartyIds,
        CancellationToken cancellationToken)
    {
        var counterpartyIds = duplicateCounterpartyIds.Append(job.MainCounterpartyId).ToArray();
        var response = await documentDiscoveryClient.DiscoverAsync(job.AccountId, counterpartyIds, job.Id, operation.Id,
            job.RequestedByUserId, job.CorrelationId, cancellationToken);
        await ReplaceDocumentSnapshotAsync(job.AccountId, counterpartyIds, response.Documents, cancellationToken);
        logger.LogInformation("MoySklad document discovery completed: operation_id={OperationId}, documents_count={DocumentsCount}",
            operation.Id, response.Documents.Count);
    }

    private async Task ReplaceDocumentSnapshotAsync(Guid accountId, IReadOnlyCollection<Guid> counterpartyIds,
        IReadOnlyList<MoySkladDocumentReference> documents, CancellationToken cancellationToken)
    {
        var knownCounterpartyIds = counterpartyIds.ToHashSet();
        var uniqueDocumentKeys = new HashSet<(string DocumentType, Guid DocumentId)>();
        var uniqueDocumentIds = new HashSet<Guid>();
        foreach (var document in documents)
        {
            if (string.IsNullOrWhiteSpace(document.DocumentType) || document.DocumentId == Guid.Empty ||
                document.CounterpartyId == Guid.Empty || !knownCounterpartyIds.Contains(document.CounterpartyId) ||
                !uniqueDocumentKeys.Add((document.DocumentType, document.DocumentId)) ||
                !uniqueDocumentIds.Add(document.DocumentId))
            {
                throw new JsonException("MoySklad document discovery response is inconsistent.");
            }
        }

        var now = timeProvider.GetUtcNow();
        var supportedDocuments = documents.Where(document => document.DocumentType != "salesreturn").ToArray();
        var rows = supportedDocuments.Select(document => new CounterpartyDocument
        {
            AccountId = accountId, CounterpartyId = document.CounterpartyId, DocumentType = document.DocumentType,
            DocumentId = document.DocumentId, UpdatedAt = now
        }).ToArray();
        var commissions = supportedDocuments.Where(document => IsCommissionReport(document.DocumentType))
            .Select(document => new DocumentAdditionalCommission { DocumentId = document.DocumentId, Contract = document.ContractId })
            .ToArray();
        await documentSnapshots.ReplaceAsync(accountId, counterpartyIds, rows, commissions, cancellationToken);
    }

    private static bool IsCommissionReport(string documentType) =>
        string.Equals(documentType, "commissionreportin", StringComparison.Ordinal) ||
        string.Equals(documentType, "commissionreportout", StringComparison.Ordinal);
}
