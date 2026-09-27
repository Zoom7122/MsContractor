using MsContractor.CatalogSyncService.Services;
using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Sync;
using MsContractor.CatalogSyncService.Models.Exceptions;

namespace MsContractor.CatalogSyncService.Consumers;

public sealed class SyncRequestedConsumer : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SyncRequestedConsumer> _logger;

    public SyncRequestedConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SyncRequestedConsumer> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = _configuration["Kafka:SyncConsumerGroup"] ?? "catalog-sync-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollIntervalMs = _configuration.GetValue("Kafka:SyncConsumerMaxPollIntervalMs", 660000)
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
                _logger.LogWarning(
                    "Kafka topic {Topic} is not available yet; retrying in 2 seconds.",
                    SyncTopics.Commands);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }
            catch (ConsumeException exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
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
                _logger.LogError(exception, "Invalid SyncRequested JSON at {TopicPartitionOffset}.", result.TopicPartitionOffset);
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
                _logger.LogError(
                    "Invalid SyncRequested at {TopicPartitionOffset}: required identifiers are missing.",
                    result.TopicPartitionOffset);
                consumer.Commit(result);
                continue;
            }

            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<ISyncProcessor>();
                await processor.ProcessAsync(command, stoppingToken);
                consumer.Commit(result);
            }
            catch (SyncRunAlreadyOwnedException exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    exception,
                    "Sync run is owned by another worker; retrying the same Kafka offset {TopicPartitionOffset} after a short delay.",
                    result.TopicPartitionOffset);
                var seekSucceeded = await RetryAtOffsetAsync(
                    topicPartition => consumer.Assignment.Contains(topicPartition),
                    consumer.Seek,
                    result.TopicPartitionOffset,
                    stoppingToken);
                if (!seekSucceeded)
                    _logger.LogInformation(
                        "Partition {TopicPartition} is no longer assigned; leaving offset uncommitted and resuming consumption.",
                        result.TopicPartitionOffset.TopicPartition);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    exception,
                    "Sync command processing did not reach a durable terminal state; offset will not be committed for {TopicPartitionOffset}.",
                    result.TopicPartitionOffset);
                var seekSucceeded = await RetryAtOffsetAsync(
                    topicPartition => consumer.Assignment.Contains(topicPartition),
                    consumer.Seek,
                    result.TopicPartitionOffset,
                    stoppingToken);
                if (!seekSucceeded)
                    _logger.LogInformation(
                        "Partition {TopicPartition} is no longer assigned; leaving offset uncommitted and resuming consumption.",
                        result.TopicPartitionOffset.TopicPartition);
            }
        }

        consumer.Close();
    }

    internal static async Task<bool> RetryAtOffsetAsync(
        Func<TopicPartition, bool> isAssigned,
        Action<TopicPartitionOffset> seek,
        TopicPartitionOffset offset,
        CancellationToken cancellationToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var seekSucceeded = false;
        if (isAssigned(offset.TopicPartition))
        {
            try
            {
                seek(offset);
                seekSucceeded = true;
            }
            catch (KafkaException exception) when (IsAssignmentLost(exception.Error.Code))
            {
                // A rebalance can revoke this partition after the assignment check but before Seek.
                // The offset remains uncommitted and the next Consume call handles reassignment.
            }
        }

        await (delay ?? Task.Delay)(TimeSpan.FromSeconds(2), cancellationToken);
        return seekSucceeded;
    }

    private static bool IsAssignmentLost(ErrorCode errorCode) =>
        errorCode is ErrorCode.Local_AssignmentLost or ErrorCode.Local_RevokePartitions or
            ErrorCode.Local_UnknownPartition or ErrorCode.Local_State;
}
