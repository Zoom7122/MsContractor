using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using MsContractor.CatalogSyncService.Repo;

namespace MsContractor.CatalogSyncService.Services;

public sealed class SyncOutboxPublisher(
    IServiceScopeFactory scopeFactory,
    IProducer<string, string> producer,
    ILogger<SyncOutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var publishFailed = false;
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<CatalogSyncDbContext>();
            var messages = await dbContext.OutboxMessages
                .Where(item => item.PublishedAt == null)
                .OrderBy(item => item.CreatedAt)
                .Take(50)
                .ToListAsync(stoppingToken);
            if (messages.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

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
                    logger.LogWarning(
                        "Could not publish sync outbox event {EventId}; attempt {Attempt}.",
                        message.Id,
                        message.PublishAttempts);
                }
            }

            await dbContext.SaveChangesAsync(stoppingToken);
            if (publishFailed)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
