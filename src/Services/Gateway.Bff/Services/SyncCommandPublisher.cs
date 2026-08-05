using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Sync;

namespace MsContractor.Gateway.Bff.Services;

public interface ISyncCommandPublisher
{
    Task PublishAsync(SyncRequested command, CancellationToken cancellationToken);
}

public sealed class SyncCommandPublisher(IProducer<string, string> producer) : ISyncCommandPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task PublishAsync(SyncRequested command, CancellationToken cancellationToken) =>
        producer.ProduceAsync(
            SyncTopics.Commands,
            new Message<string, string>
            {
                Key = command.AccountId.ToString("D"),
                Value = JsonSerializer.Serialize(command, JsonOptions)
            },
            cancellationToken);
}
