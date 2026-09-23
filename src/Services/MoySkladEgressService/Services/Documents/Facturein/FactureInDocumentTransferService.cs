using MsContractor.MoySkladEgressService.Gateways.Documents.Facturein;
using MsContractor.MoySkladEgressService.Models;
using MsContractor.MoySkladEgressService.Models.Exceptions;
using MsContractor.MoySkladEgressService.Models.Options;
using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services.Documents.Facturein;

public sealed record FactureInTransferResult(
    IReadOnlyList<Guid> TransferredDocumentIds,
    IReadOnlyList<FactureInFailedDocumentResult> FailedDocuments);

public interface IFactureInDocumentTransferService
{
    Task<FactureInTransferResult> TransferAsync(Guid accountId,
        IReadOnlyCollection<FactureInRecreationItem> documents,
        CancellationToken cancellationToken);
}

public sealed class FactureInDocumentTransferService : IFactureInDocumentTransferService
{
    private readonly IMoySkladFactureInGateway _gateway;
    private readonly IFactureInRecreationItemRepository _items;
    private readonly FactureInRecreationOptions _options;

    public FactureInDocumentTransferService(IMoySkladFactureInGateway gateway,
        IFactureInRecreationItemRepository items, FactureInRecreationOptions options)
    {
        _gateway = gateway;
        _items = items;
        _options = options;
    }

    public async Task<FactureInTransferResult> TransferAsync(Guid accountId,
        IReadOnlyCollection<FactureInRecreationItem> documents,
        CancellationToken cancellationToken)
    {
        foreach (var batch in documents.Where(item => item.Stage is "Prepared" or "DeleteFailed")
                     .Chunk(MoySkladFactureInGateway.BatchSize))
            await DeleteAsync(accountId, batch, cancellationToken);

        foreach (var batch in documents.Where(item => item.Stage is "Deleted" or "CreateFailed")
                     .Chunk(MoySkladFactureInGateway.BatchSize))
            await CreateAsync(accountId, batch, cancellationToken);

        return new FactureInTransferResult(
            documents.Where(item => item.Stage == "Completed")
                .Select(item => item.SourceFactureInId).ToArray(),
            documents.Where(item => item.Stage is "DeleteFailed" or "CreateFailed" or "ResponseMappingFailed")
                .Select(ToFailed).ToArray());
    }

    private async Task DeleteAsync(Guid accountId, IReadOnlyList<FactureInRecreationItem> batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.ToDictionary(item => item.SourceFactureInId);
        for (var attempt = 1; pending.Count > 0 && attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MoySkladFactureInBatchDeleteResult> response;
            try
            {
                response = await _gateway.DeleteBatchAsync(accountId, Guid.NewGuid().ToString("D"),
                    pending.Keys.ToArray(), cancellationToken);
            }
            catch (EgressException exception) when (exception.Retryable && attempt < _options.MaxAttempts)
            {
                await DelayAsync(attempt, cancellationToken);
                continue;
            }

            var byId = response.GroupBy(item => item.SourceDocumentId)
                .ToDictionary(group => group.Key, group => group.Last());
            var retry = new Dictionary<Guid, FactureInRecreationItem>();
            foreach (var item in pending.Values)
            {
                if (!byId.TryGetValue(item.SourceFactureInId, out var result))
                {
                    await FailAsync(item, "DeleteFailed", "FACTUREIN_DELETE_RESPONSE_INVALID",
                        "MoySklad did not return a delete result for facturein.", cancellationToken);
                    retry[item.SourceFactureInId] = item;
                    continue;
                }

                if (result.Succeeded)
                {
                    item.Stage = "Deleted";
                    item.ErrorCode = null;
                    item.Error = null;
                    await _items.SaveAsync(item, cancellationToken);
                    continue;
                }

                await FailAsync(item, "DeleteFailed", result.ErrorCode ?? "FACTUREIN_DELETE_FAILED",
                    result.Error ?? "MoySklad rejected facturein deletion.", cancellationToken);
                retry[item.SourceFactureInId] = item;
            }
            pending = retry;
            if (pending.Count > 0 && attempt < _options.MaxAttempts)
                await DelayAsync(attempt, cancellationToken);
        }
    }

    private async Task CreateAsync(Guid accountId, IReadOnlyList<FactureInRecreationItem> batch,
        CancellationToken cancellationToken)
    {
        var pending = batch.ToDictionary(item => item.SourceFactureInId);
        for (var attempt = 1; pending.Count > 0 && attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pending.Values.Select(item => new MoySkladFactureInBatchCreateItem(
                item.SourceFactureInId, item.NewSyncId, item.PayloadJson)).ToArray();
            IReadOnlyList<MoySkladFactureInBatchCreateResult> response;
            try
            {
                response = await _gateway.CreateBatchAsync(accountId, Guid.NewGuid().ToString("D"), request, cancellationToken);
            }
            catch (EgressException exception) when (exception.Retryable && attempt < _options.MaxAttempts)
            {
                await ResolveUnknownCreateAsync(accountId, pending.Values, cancellationToken);
                pending = pending.Where(pair => pair.Value.Stage != "Completed")
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                if (pending.Count > 0)
                    await DelayAsync(attempt, cancellationToken);
                continue;
            }

            var bySyncId = response.Where(item => item.ReturnedSyncId is not null)
                .GroupBy(item => item.ReturnedSyncId!.Value)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var retry = new Dictionary<Guid, FactureInRecreationItem>();
            foreach (var item in pending.Values)
            {
                if (!bySyncId.TryGetValue(item.NewSyncId, out var results) || results.Length != 1)
                {
                    await FailAsync(item, "ResponseMappingFailed", "FACTUREIN_RESPONSE_MAPPING_FAILED",
                        "MoySklad did not return an unambiguous syncId for facturein creation.", cancellationToken);
                    continue;
                }

                var result = results[0];
                if (result.ErrorCode is not null)
                {
                    await FailAsync(item, "CreateFailed", result.ErrorCode,
                        result.Error ?? "MoySklad rejected facturein creation.", cancellationToken);
                    retry[item.SourceFactureInId] = item;
                    continue;
                }
                if (result.DocumentId is null || result.DocumentId == Guid.Empty ||
                    !string.Equals(result.DocumentType, "facturein", StringComparison.OrdinalIgnoreCase))
                {
                    await FailAsync(item, "ResponseMappingFailed", "FACTUREIN_RESPONSE_MAPPING_FAILED",
                        "MoySklad returned an incomplete or invalid facturein creation result.", cancellationToken);
                    continue;
                }

                item.NewFactureInId = result.DocumentId;
                item.Stage = "Completed";
                item.ErrorCode = null;
                item.Error = null;
                await _items.SaveAsync(item, cancellationToken);
            }
            pending = retry;
            if (pending.Count > 0 && attempt < _options.MaxAttempts)
                await DelayAsync(attempt, cancellationToken);
        }
    }

    private async Task ResolveUnknownCreateAsync(Guid accountId, IEnumerable<FactureInRecreationItem> pending,
        CancellationToken cancellationToken)
    {
        var documents = pending.ToArray();
        var found = await _gateway.FindBySyncIdsAsync(accountId, Guid.NewGuid().ToString("D"),
            documents.Select(item => item.NewSyncId).ToArray(), cancellationToken);
        foreach (var item in documents)
        {
            if (!found.TryGetValue(item.NewSyncId, out var documentId))
                continue;
            item.NewFactureInId = documentId;
            item.Stage = "Completed";
            item.ErrorCode = null;
            item.Error = null;
            await _items.SaveAsync(item, cancellationToken);
        }
    }

    private async Task FailAsync(FactureInRecreationItem item, string stage, string code, string error,
        CancellationToken cancellationToken)
    {
        item.Stage = stage;
        item.ErrorCode = code;
        item.Error = error;
        await _items.SaveAsync(item, cancellationToken);
    }

    private Task DelayAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(_options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)), cancellationToken);

    private static FactureInFailedDocumentResult ToFailed(FactureInRecreationItem item) =>
        new(item.SourceFactureInId, item.NewSyncId, item.NewFactureInId, "Failed", item.ErrorCode, item.Error);
}
