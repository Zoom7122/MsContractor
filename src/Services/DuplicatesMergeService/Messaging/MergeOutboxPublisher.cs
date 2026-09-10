using MsContractor.DuplicatesMergeService.Repositories;
using Confluent.Kafka;

namespace MsContractor.DuplicatesMergeService.Messaging;

public sealed class MergeOutboxPublisher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IProducer<string, string> _producer;
    private readonly ILogger<MergeOutboxPublisher> _logger;

    public MergeOutboxPublisher(
        IServiceScopeFactory scopeFactory,
        IProducer<string, string> producer,
        ILogger<MergeOutboxPublisher> logger)
    {
        _scopeFactory = scopeFactory;
        _producer = producer;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IMergeOutboxRepository>();
            var messages = await repository.GetPendingAsync(stoppingToken);
            if (messages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            var failed = false;
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
                            Headers = [new Header("event-type", System.Text.Encoding.UTF8.GetBytes(message.EventType))]
                        },
                        stoppingToken);
                    message.PublishedAt = DateTimeOffset.UtcNow;
                    message.LastError = null;
                }
                catch (ProduceException<string, string> exception)
                {
                    failed = true;
                    message.PublishAttempts++;
                    message.LastError = exception.Error.Code.ToString();
                    _logger.LogWarning(
                        "Could not publish merge outbox message {MessageId}; attempt {Attempt}.",
                        message.Id, message.PublishAttempts);
                }
            }

            await repository.SavePublicationResultsAsync(stoppingToken);
            if (failed)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
