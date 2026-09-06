using MsContractor.DuplicatesMergeService.Models.Exceptions;
using MsContractor.CatalogSyncService.Models;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed partial class MergeProcessor
{
    /// <summary>
    /// Запроса на отправку в engress на обновление КА
    /// </summary>
    /// <param name="job"></param>
    /// <param name="operation"></param>
    /// <param name="snapshot"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="MergeRetryableException"></exception>
    private async Task<bool> ExecuteUpdateMainAsync(
        MergeJob job,
        MergeOperation operation,
        MergeMainCounterpartyDto snapshot,
        CancellationToken cancellationToken)
    {
        await MarkRunningAsync(operation, cancellationToken);
        try
        {
            var response = await egressClient.UpdateAsync(
                job.AccountId, operation.CounterpartyId, snapshot,
                job.Id, operation.Id, job.RequestedByUserId, job.CorrelationId, cancellationToken);

            var parsed = parser.ParseOne(response.Json);

            EnsureResponse(operation.CounterpartyId, parsed, archivedRequired: false);

            var local = await FindLocalAsync(job.AccountId, operation.CounterpartyId, cancellationToken);

            normalizer.Apply(local, parsed, timeProvider.GetUtcNow());
            Complete(operation);
            await repository.SaveProgressAsync(job.AccountId, cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            if (await HandleFailureAsync(operation, exception, cancellationToken))
                throw new MergeRetryableException("Update main counterparty will be retried.", exception);
            await FailJobAsync(job, cancellationToken);
            return false;
        }
    }
}
