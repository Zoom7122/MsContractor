using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Services;

public sealed class MergeRequestedConsumer(
    IServiceScopeFactory scopeFactory,
    IProducer<string, string> producer,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<MergeRequestedConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = configuration["Kafka:BootstrapServers"] ?? "localhost:9092",
            GroupId = configuration["Kafka:MergeConsumerGroup"] ?? "duplicates-merge-service",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollIntervalMs = configuration.GetValue("Kafka:MergeConsumerMaxPollIntervalMs", 660000)
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(MergeTopics.Commands);

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
            catch (ConsumeException exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Kafka merge consume failed; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                continue;
            }

            MergeRequested? command;
            try
            {
                command = JsonSerializer.Deserialize<MergeRequested>(result.Message.Value, JsonOptions);
            }
            catch (JsonException)
            {
                await DeadLetterOrRetryAsync(consumer, result, null, "INVALID_JSON", stoppingToken);
                continue;
            }

            if (!IsValid(command))
            {
                await DeadLetterOrRetryAsync(
                    consumer, result, command?.MessageId, "INVALID_MERGE_COMMAND", stoppingToken);
                continue;
            }

            try
            {
                //Запуск Merge операции
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMergeProcessor>()
                    .ProcessAsync(command!, stoppingToken);
                consumer.Commit(result);
            }
            catch (MergeCommandRejectedException)
            {
                logger.LogError(
                    "Merge command rejected: message_id={MessageId}, merge_job_id={MergeJobId}, account_id={AccountId}, error_code={ErrorCode}",
                    command!.MessageId, command.MergeJobId, command.AccountId, "MERGE_COMMAND_REJECTED");
                await DeadLetterOrRetryAsync(
                    consumer, result, command.MessageId, "MERGE_COMMAND_REJECTED", stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "Merge processing did not reach durable terminal state; offset will not be committed: message_id={MessageId}, merge_job_id={MergeJobId}, account_id={AccountId}",
                    command!.MessageId, command.MergeJobId, command.AccountId);
                consumer.Seek(result.TopicPartitionOffset);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }

        consumer.Close();
    }

    private async Task DeadLetterOrRetryAsync(
        IConsumer<string, string> consumer,
        ConsumeResult<string, string> result,
        Guid? messageId,
        string errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(result.Message.Value ?? string.Empty);
            var deadLetter = new MergeDeadLetter(
                Guid.NewGuid(), messageId, result.Topic, result.Partition.Value, result.Offset.Value,
                errorCode, timeProvider.GetUtcNow(), Convert.ToHexStringLower(SHA256.HashData(payloadBytes)));
            await producer.ProduceAsync(
                MergeTopics.DeadLetters,
                new Message<string, string>
                {
                    Key = messageId?.ToString("D") ?? result.Message.Key ?? "unknown",
                    Value = JsonSerializer.Serialize(deadLetter, JsonOptions)
                },
                cancellationToken);
            consumer.Commit(result);
        }
        catch (ProduceException<string, string> exception)
        {
            logger.LogWarning(
                exception,
                "Could not publish merge dead letter; source offset will not be committed: topic={Topic}, partition={Partition}, offset={Offset}, error_code={ErrorCode}",
                result.Topic, result.Partition.Value, result.Offset.Value, errorCode);
            consumer.Seek(result.TopicPartitionOffset);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private static bool IsValid(MergeRequested? command) =>
        command is
        {
            SchemaVersion: 1,
            MessageId: var messageId,
            CorrelationId: var correlationId,
            MergeJobId: var mergeJobId,
            AccountId: var accountId,
            MainCounterpartyId: var mainId,
            RequestedByUserId: var userId,
            RequestedAt: var requestedAt,
            MainCounterparty: not null,
            DuplicateCounterpartyIds: not null
        } &&
        messageId != Guid.Empty && correlationId != Guid.Empty && mergeJobId != Guid.Empty &&
        accountId != Guid.Empty && mainId != Guid.Empty && userId != Guid.Empty &&
        requestedAt != default && !string.IsNullOrWhiteSpace(command.MainCounterparty.Name) &&
        command.DuplicateCounterpartyIds.Count > 0;
}
