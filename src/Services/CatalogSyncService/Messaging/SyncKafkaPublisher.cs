using MsContractor.CatalogSyncService.Models.Exceptions;
using System.Text.Json;
using Confluent.Kafka;
using MsContractor.Contracts.Sync;

namespace MsContractor.CatalogSyncService.Messaging;

public interface ISyncKafkaPublisher
{
    Task PublishAsync(SyncRequested command, CancellationToken cancellationToken);
}

public sealed class SyncKafkaPublisher : ISyncKafkaPublisher
{
    private readonly IProducer<string, string> _producer;

    public SyncKafkaPublisher(
        IProducer<string, string> producer)
    {
        _producer = producer;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(
        SyncRequested command,
        CancellationToken cancellationToken)
    {
        try
        {
            await _producer.ProduceAsync(
            SyncTopics.Commands,
            new Message<string, string>
            {
                Key = command.AccountId.ToString("D"),
                Value = JsonSerializer.Serialize(command, JsonOptions)
            },
            cancellationToken);
        }
        catch (ProduceException<string, string> exception)
        {
            throw new SyncQueueUnavailableException(exception);
        }
    }
}
