using System.Text.Json;

namespace MsContractor.VendorService.Repo;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }

    public string RequestId { get; set; } = null!;

    public Guid AccountId { get; set; }

    public string EventType { get; set; } = null!;

    public JsonDocument Payload { get; set; } = null!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PublishedAt { get; set; }

    public int PublishAttempts { get; set; }

    public string? LastError { get; set; }
}
