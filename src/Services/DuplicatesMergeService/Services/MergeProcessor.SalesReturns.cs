using System.Text.Json;
using System.Text.Json.Nodes;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Internal;
using MsContractor.DuplicatesMergeService.Models;
using MsContractor.DuplicatesMergeService.Models.Exceptions;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed partial class MergeProcessor
{
    private async Task ExecuteSalesReturnRecreationAsync(MergeJob job, MergeOperation operation,
        IReadOnlyList<CounterpartyDocument> rows, CancellationToken cancellationToken)
    {
        if (operation.SalesReturnRequestJson is null)
        {
            if (rows.Count == 0) return;
            var raw = await documentSnapshots.GetAdditionalDataAsync(job.AccountId, rows.Select(x => x.DocumentId).ToArray(), cancellationToken);
            var documents = new List<RecreateSalesReturnItem>();
            foreach (var row in rows)
            {
                if (!raw.TryGetValue(row.DocumentId, out var json))
                    throw new MergeEgressException("SALESRETURN_SNAPSHOT_MISSING", "Salesreturn copy data was not discovered.", 422);
                SalesReturnCopyData? data;
                try { data = JsonSerializer.Deserialize<SalesReturnCopyData>(json, JsonOptions); }
                catch (JsonException)
                { throw new MergeEgressException("SALESRETURN_SNAPSHOT_INVALID", "Salesreturn snapshot must contain a complete positions array.", 422); }
                if (data?.Positions is null || data.Positions.Length == 0 || data.Organization is null || data.Store is null)
                    throw new MergeEgressException("SALESRETURN_SNAPSHOT_INCOMPLETE", "Salesreturn snapshot lacks organization, store or complete positions.", 422);
                documents.Add(new RecreateSalesReturnItem(row.DocumentId, row.CounterpartyId, null, null, data));
            }
            operation.SalesReturnRequestJson = JsonSerializer.Serialize(new RecreateSalesReturnsRequest(job.MainCounterpartyId, documents), JsonOptions);
            // Keep the complete original selection, including subsequently completed items, for Egress idempotency.
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
        }
        var request = JsonSerializer.Deserialize<RecreateSalesReturnsRequest>(operation.SalesReturnRequestJson, JsonOptions)!;
        var response = await documentChangeClient.RecreateSalesReturnsAsync(job.AccountId, request, job.Id,
            operation.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);
        var originals = request.Documents.ToDictionary(x => x.OldDocumentId);
        if (response.OperationId != operation.Id || response.MainCounterpartyId != job.MainCounterpartyId || response.Documents is null ||
            response.Documents.Count != originals.Count || response.Documents.Any(x => x is null || !originals.ContainsKey(x.OldDocumentId)) ||
            response.Documents.Select(x => x.OldDocumentId).Distinct().Count() != originals.Count ||
            response.Documents.Any(x => x.Status is not ("Completed" or "Pending" or "Failed")))
            throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "Egress returned inconsistent salesreturn results.", 502);
        var completed = response.Documents.Where(x => x.Status == "Completed").ToArray();
        if (completed.Any(x => x.Stage != "Completed" || x.ErrorCode is not null || x.NewDocumentId is null || x.NewDocumentId == Guid.Empty ||
                originals.ContainsKey(x.NewDocumentId.Value) || x.Data?.Positions is null || x.Data.Positions.Length == 0) ||
            completed.Select(x => x.NewDocumentId).Distinct().Count() != completed.Length)
            throw new MergeEgressException("EGRESS_INVALID_RESPONSE", "Egress returned invalid recreated salesreturn identities or copy data.", 502);
        await documentSnapshots.ApplyRecreatedAsync(job.AccountId, job.MainCounterpartyId, completed.Select(x =>
        {
            var snapshot = JsonSerializer.SerializeToNode(x.Data, JsonOptions)!.AsObject();
            snapshot["id"] = x.NewDocumentId!.Value;
            return new RecreatedDocumentSnapshot(x.OldDocumentId, x.NewDocumentId.Value,
                originals[x.OldDocumentId].DuplicateCounterpartyId, snapshot.ToJsonString(), timeProvider.GetUtcNow());
        }).ToArray(), cancellationToken);
        var failures = response.Documents.Where(x => x.Status != "Completed").ToArray();
        if (failures.Length > 0)
        {
            var retryable = failures.All(x => x.Retryable);
            var failure = failures.FirstOrDefault(x => !x.Retryable) ?? failures[0];
            throw new MergeEgressException(failure.ErrorCode ?? "SALESRETURN_RECREATION_PENDING",
                failure.Error ?? "Salesreturn recreation has not completed.", retryable ? 503 : 422);
        }
    }
}
