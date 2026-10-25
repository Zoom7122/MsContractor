using System.Security.Cryptography;
using System.Text;

namespace MsContractor.CatalogSyncService.Models;

public sealed class SyncRunError
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Guid SyncRunId { get; set; }
    public Guid? CounterpartyId { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public string ErrorKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public static SyncRunError Create(
        Guid accountId, Guid syncRunId, Guid? counterpartyId, string reasonCode,
        string message, DateTimeOffset now, string discriminator = "") => new()
    {
        Id = Guid.NewGuid(),
        AccountId = accountId,
        SyncRunId = syncRunId,
        CounterpartyId = counterpartyId,
        ReasonCode = reasonCode,
        ErrorMessage = message.Replace('\0', '\uFFFD'),
        CreatedAt = now,
        ErrorKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{counterpartyId:D}\n{reasonCode}\n{discriminator}")))
    };
}
