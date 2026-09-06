namespace MsContractor.CatalogSyncService.Models;

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public string ConsumerName { get; set; } = null!;
    public DateTimeOffset ProcessedAt { get; set; }
}
