namespace MsContractor.CatalogSyncService.Models;

public sealed class SyncOutboxMessage
{
    public Guid Id { get; set; }
    public string Topic { get; set; } = null!;
    public string MessageKey { get; set; } = null!;
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int PublishAttempts { get; set; }
    public string? LastError { get; set; }
}
