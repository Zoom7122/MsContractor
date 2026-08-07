using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed class MergeOutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IProducer<string, string> producer,
    ILogger<MergeOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
            var messages = await dbContext.OutboxMessages
                .Where(item => item.PublishedAt == null && item.EventType == nameof(MergeRequested))
                .OrderBy(item => item.CreatedAt)
                .Take(50)
                .ToListAsync(stoppingToken);
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
                    await producer.ProduceAsync(
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
                    logger.LogWarning(
                        "Could not publish merge outbox message {MessageId}; attempt {Attempt}.",
                        message.Id, message.PublishAttempts);
                }
            }

            await dbContext.SaveChangesAsync(stoppingToken);
            if (failed)
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
