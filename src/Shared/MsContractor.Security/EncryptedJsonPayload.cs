namespace MsContractor.Security;

/// <summary>
/// An AES-GCM encrypted JSON payload.
/// </summary>
/// <param name="Ciphertext">The encrypted UTF-8 JSON document.</param>
/// <param name="Nonce">The 12-byte nonce used to encrypt the JSON document.</param>
/// <param name="Tag">The 16-byte authentication tag.</param>
/// <param name="KeyVersion">The payload-encryption version used for this operation.</param>
public sealed record EncryptedJsonPayload(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag,
    int KeyVersion);
