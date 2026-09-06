using MsContractor.MoySkladEgressService.Repositories;

namespace MsContractor.MoySkladEgressService.Services;

public sealed class SalesReturnRecoveryWorker(IServiceScopeFactory scopes, TimeProvider timeProvider,
    ILogger<SalesReturnRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scan = scopes.CreateAsyncScope();
                var keys = await scan.ServiceProvider.GetRequiredService<ISalesReturnOperationRepository>()
                    .GetDueAsync(timeProvider.GetUtcNow(), stoppingToken);
                foreach (var key in keys)
                {
                    try
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<SalesReturnRecreationService>().ResumeAsync(key, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception exception)
                    {
                        logger.LogWarning("Salesreturn recovery failed account_id={AccountId} operation_id={OperationId} error_type={ErrorType}",
                            key.AccountId, key.OperationId, exception.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            { logger.LogWarning("Salesreturn recovery scan failed error_type={ErrorType}", exception.GetType().Name); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) return; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
