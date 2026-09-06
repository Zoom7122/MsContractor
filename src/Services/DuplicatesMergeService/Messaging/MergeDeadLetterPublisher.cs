using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Merge;

namespace MsContractor.DuplicatesMergeService.Messaging;

public interface IMergeDeadLetterPublisher
{
    Task PublishAsync(ConsumeResult<string, string> result, Guid? messageId, string errorCode, CancellationToken cancellationToken);
}

public sealed class MergeDeadLetterPublisher(IProducer<string, string> producer, TimeProvider timeProvider) : IMergeDeadLetterPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(ConsumeResult<string, string> result, Guid? messageId, string errorCode, CancellationToken cancellationToken)
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
    }
}
