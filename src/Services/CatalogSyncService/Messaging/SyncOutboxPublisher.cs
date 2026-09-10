using MsContractor.CatalogSyncService.Repositories;
using Confluent.Kafka;

namespace MsContractor.CatalogSyncService.Messaging;

public sealed class SyncOutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<SyncOutboxPublisher> _logger;

    public SyncOutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IProducer<string, string> producer,
        ILogger<SyncOutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var publishFailed = false;
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISyncOutboxRepository>();
            var messages = await repository.GetPendingAsync(stoppingToken);
            if (messages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            foreach (var message in messages)
            {
                try
                {
                    await _producer.ProduceAsync(
                        message.Topic,
                        new Message<string, string>
                        {
                            Key = message.MessageKey,
                            Value = message.Payload,
                            Headers =
                            [
                                new Header(
                                    "event-type",
                                    System.Text.Encoding.UTF8.GetBytes(message.EventType))
                            ]
                        },
                        stoppingToken);
                    message.PublishedAt = DateTimeOffset.UtcNow;
                    message.LastError = null;
                }
                catch (ProduceException<string, string> exception)
                {
                    publishFailed = true;
                    message.PublishAttempts++;
                    message.LastError = exception.Error.Code.ToString();
                    _logger.LogWarning(
                        "Could not publish sync outbox event {EventId}; attempt {Attempt}.",
                        message.Id,
                        message.PublishAttempts);
                }
            }

            await repository.SavePublicationResultsAsync(stoppingToken);
            if (publishFailed)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
