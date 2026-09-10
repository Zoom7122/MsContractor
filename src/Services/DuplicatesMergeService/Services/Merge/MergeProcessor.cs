using MsContractor.CatalogSyncService.Models;
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
        await state.StartJobAsync(job, cancellationToken);

        var discovery = job.Operations.Single(item => item.OperationType == MergeOperationTypes.DiscoverDocuments);
        if (!await state.ExecuteAsync(job, discovery,
                token => documentDiscovery.DiscoverAsync(job, discovery, command.DuplicateCounterpartyIds, token),
                "Document discovery will be retried.", cancellationToken))
            return;

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
    }
}
