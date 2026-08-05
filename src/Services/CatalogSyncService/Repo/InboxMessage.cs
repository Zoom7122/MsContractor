namespace MsContractor.CatalogSyncService.Repo;

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public string ConsumerName { get; set; } = null!;
    public DateTimeOffset ProcessedAt { get; set; }
}
