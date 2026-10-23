namespace MsContractor.Security;

/// <summary>
/// An AES-GCM wrapped data-encryption key.
/// </summary>
/// <param name="Ciphertext">The encrypted DEK.</param>
/// <param name="Nonce">The 12-byte nonce used to wrap the DEK.</param>
/// <param name="Tag">The 16-byte authentication tag.</param>
/// <param name="KeyVersion">The KEK version used to wrap the DEK.</param>
public sealed record EncryptedDataKey(
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] Tag,
    int KeyVersion);
