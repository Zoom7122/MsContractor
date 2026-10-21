namespace MsContractor.Security;

/// <summary>
/// Identifies the tenant, record, purpose, and key version bound to encrypted data.
/// </summary>
/// <param name="AccountId">The tenant account identifier.</param>
/// <param name="RecordId">The identifier of the record containing the encrypted data.</param>
/// <param name="Purpose">The purpose for which the key is used.</param>
/// <param name="KeyVersion">The version of the key used by the operation.</param>
public sealed record EncryptionContext(
    Guid AccountId,
    string RecordId,
    string Purpose,
    int KeyVersion);
