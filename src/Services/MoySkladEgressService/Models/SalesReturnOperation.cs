using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MsContractor.MoySkladEgressService.Models;

public sealed class SalesReturnOperation
{
    public Guid AccountId { get; set; }
    public Guid OperationId { get; set; }
    public Guid JobId { get; set; }
    public Guid UserId { get; set; }
    public Guid MainCounterpartyId { get; set; }
    public string CorrelationId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string RequestJson { get; set; } = "";
    public string ItemsJson { get; set; } = "[]";
    public DateTimeOffset? NextAttemptAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    [NotMapped] public List<SalesReturnOperationItem> Items { get; set; } = [];
    public void SerializeItems() => ItemsJson = JsonSerializer.Serialize(Items);
    public void DeserializeItems() => Items = JsonSerializer.Deserialize<List<SalesReturnOperationItem>>(ItemsJson)!;
}

public sealed class SalesReturnOperationItem
{
    public Guid OldDocumentId { get; set; }
    public Guid SyncId { get; set; }
    public Guid? NewDocumentId { get; set; }
    [JsonIgnore] public string Payload { get; set; } = "";
    // Validate -> Delete -> Deleting -> Create -> Creating -> Completed.
    // Intent stages are persisted before every remote mutation.
    public string Stage { get; set; } = "Validate";
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public bool Retryable { get; set; } = true;
    [JsonIgnore] public bool Pending => Stage != "Completed" && (ErrorCode is null || Retryable);
}

public sealed class SalesReturnClaim
{
    public string Payload { get; set; } = "";
    public Guid AccountId { get; set; }
    public Guid OldDocumentId { get; set; }
    public Guid OperationId { get; set; }
}

public sealed record SalesReturnOperationKey(Guid AccountId, Guid OperationId);
public sealed record SalesReturnCallContext(Guid AccountId, Guid UserId, Guid JobId, Guid OperationId, string CorrelationId);
public sealed record SalesReturnCreated(Guid SyncId, Guid? DocumentId, string? ErrorCode = null,
    string? Error = null, bool Retryable = false);
