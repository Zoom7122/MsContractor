namespace MsContractor.AuditService.Services;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;

    public Worker(
        ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
            }
            await Task.Delay(1000, stoppingToken);
        }
    }
}
