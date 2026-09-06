using System.Text.Json;

namespace MsContractor.VendorService.Models;

public sealed class Installation
{
    public Guid AccountId { get; set; }

    public Guid AppId { get; set; }

    public string AppUid { get; set; } = null!;

    public string? AccountName { get; set; }

    public string Status { get; set; } = null!;

    public byte[]? AccessTokenCiphertext { get; set; }

    public byte[]? AccessTokenNonce { get; set; }

    public byte[]? AccessTokenTag { get; set; }

    public int? TokenKeyVersion { get; set; }

    public JsonDocument? AccessScope { get; set; }

    public JsonDocument? Subscription { get; set; }

    public DateTimeOffset InstalledAt { get; set; }

    public DateTimeOffset? ActivatedAt { get; set; }

    public DateTimeOffset? DeactivatedAt { get; set; }

    public DateTimeOffset? LastContextAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
