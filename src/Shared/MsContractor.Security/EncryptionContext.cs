namespace MsContractor.Security;

/// <summary>
/// Identifies the tenant, record, purpose, and key version bound to encrypted data.
/// </summary>
/// <param name="AccountId">The tenant account identifier.</param>
/// <param name="RecordId">The identifier of the record containing the encrypted data.</param>
/// <param name="Purpose">The purpose for which the key is used.</param>
/// <param name="KeyVersion">
/// The key version bound to the operation: the KEK version when wrapping a DEK,
/// or the JSON-encryption version when encrypting a payload.
/// </param>
public sealed record EncryptionContext(
    Guid AccountId,
    string RecordId,
    string Purpose,
    int KeyVersion);
