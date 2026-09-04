using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Sync;

namespace MsContractor.CatalogSyncService.Services;

public sealed class SyncRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<SyncRequestedConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = configuration["Kafka:SyncConsumerGroup"] ?? "catalog-sync-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(SyncTopics.Commands);

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string> result;
            try
            {
                result = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ConsumeException exception) when (
                exception.Error.Code == ErrorCode.UnknownTopicOrPart &&
                !stoppingToken.IsCancellationRequested)
            {
                // The broker may still be creating the topic on a fresh deployment.
                // Do not let this transient state stop the whole CatalogSync host.
                logger.LogWarning(
                    "Kafka topic {Topic} is not available yet; retrying in 2 seconds.",
                    SyncTopics.Commands);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            catch (ConsumeException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Kafka consume failed for topic {Topic}; retrying in 2 seconds.",
                    SyncTopics.Commands);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            SyncRequested? command;
            try
            {
                command = JsonSerializer.Deserialize<SyncRequested>(result.Message.Value, JsonOptions);
            }
            catch (JsonException exception)
            {
                logger.LogError(exception, "Invalid SyncRequested JSON at {TopicPartitionOffset}.", result.TopicPartitionOffset);
                consumer.Commit(result);
                continue;
            }

            if (command is null ||
                command.MessageId == Guid.Empty ||
                command.SyncRunId == Guid.Empty ||
                command.AccountId == Guid.Empty ||
                command.RequestedByUserId == Guid.Empty ||
                command.RequestedAt == default ||
                !Enum.IsDefined(command.Mode))
            {
                logger.LogError(
                    "Invalid SyncRequested at {TopicPartitionOffset}: required identifiers are missing.",
                    result.TopicPartitionOffset);
                consumer.Commit(result);
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ISyncProcessor>();
                await processor.ProcessAsync(command, stoppingToken);
                consumer.Commit(result);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Sync command processing did not reach a durable terminal state; offset will not be committed for {TopicPartitionOffset}.",
                    result.TopicPartitionOffset);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        consumer.Close();
    }
}
