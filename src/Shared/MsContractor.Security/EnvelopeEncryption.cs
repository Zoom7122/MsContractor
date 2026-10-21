using System.Security.Cryptography;
using System.Text.Json;

namespace MsContractor.Security;

/// <summary>
/// Generates data-encryption keys and encrypts DEKs and JSON payloads with AES-256-GCM.
/// </summary>
public static class EnvelopeEncryption
{
    public const int KeySizeInBytes = 32;
    public const int NonceSizeInBytes = 12;
    public const int TagSizeInBytes = 16;

    /// <summary>
    /// Generates a cryptographically random 256-bit data-encryption key.
    /// </summary>
    public static byte[] GenerateDataEncryptionKey() => RandomNumberGenerator.GetBytes(KeySizeInBytes);

    /// <summary>
    /// Encrypts a DEK with a KEK using AES-256-GCM.
    /// The context key version must identify the KEK version.
    /// </summary>
    public static EncryptedDataKey EncryptDataKey(
        ReadOnlySpan<byte> dataEncryptionKey,
        ReadOnlySpan<byte> keyEncryptionKey,
        EncryptionContext context)
    {
        ValidateKey(dataEncryptionKey, nameof(dataEncryptionKey));
        ValidateKey(keyEncryptionKey, nameof(keyEncryptionKey));
        EncryptionContextValidator.Validate(context);

        var associatedData = AssociatedDataBuilder.Build(context);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var ciphertext = new byte[dataEncryptionKey.Length];
        var tag = new byte[TagSizeInBytes];

        using var aes = new AesGcm(keyEncryptionKey, TagSizeInBytes);
        aes.Encrypt(nonce, dataEncryptionKey, ciphertext, tag, associatedData);

        return new EncryptedDataKey(ciphertext, nonce, tag, context.KeyVersion);
    }

    /// <summary>
    /// Decrypts a wrapped DEK with its KEK using AES-256-GCM.
    /// </summary>
    public static byte[] DecryptDataKey(
        EncryptedDataKey encryptedDataKey,
        ReadOnlySpan<byte> keyEncryptionKey,
        EncryptionContext context)
    {
        ArgumentNullException.ThrowIfNull(encryptedDataKey);
        ValidateKey(keyEncryptionKey, nameof(keyEncryptionKey));
        EncryptionContextValidator.Validate(context);
        ValidateEnvelope(
            encryptedDataKey.Ciphertext,
            encryptedDataKey.Nonce,
            encryptedDataKey.Tag,
            nameof(encryptedDataKey));
        if (encryptedDataKey.Ciphertext.Length != KeySizeInBytes)
            throw new ArgumentException("Wrapped DEK ciphertext must contain exactly 32 bytes.", nameof(encryptedDataKey));
        EnsureKeyVersion(encryptedDataKey.KeyVersion, context.KeyVersion, "wrapped DEK");

        var associatedData = AssociatedDataBuilder.Build(context);
        var dataEncryptionKey = new byte[KeySizeInBytes];
        var decrypted = false;

        try
        {
            using var aes = new AesGcm(keyEncryptionKey, TagSizeInBytes);
            aes.Decrypt(
                encryptedDataKey.Nonce,
                encryptedDataKey.Ciphertext,
                encryptedDataKey.Tag,
                dataEncryptionKey,
                associatedData);
            decrypted = true;
            return dataEncryptionKey;
        }
        finally
        {
            if (!decrypted)
                CryptographicOperations.ZeroMemory(dataEncryptionKey);
        }
    }

    /// <summary>
    /// Serializes a value as UTF-8 JSON and encrypts it with a DEK using AES-256-GCM.
    /// The context key version is stored with the payload and included in its AAD.
    /// </summary>
    public static EncryptedJsonPayload EncryptJson<T>(
        T value,
        ReadOnlySpan<byte> dataEncryptionKey,
        EncryptionContext context,
        JsonSerializerOptions? serializerOptions = null)
    {
        ValidateKey(dataEncryptionKey, nameof(dataEncryptionKey));
        EncryptionContextValidator.Validate(context);

        var associatedData = AssociatedDataBuilder.Build(context);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeInBytes);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeInBytes];

        try
        {
            using var aes = new AesGcm(dataEncryptionKey, TagSizeInBytes);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            return new EncryptedJsonPayload(ciphertext, nonce, tag, context.KeyVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Decrypts and deserializes a JSON payload with its DEK using AES-256-GCM.
    /// </summary>
    public static T? DecryptJson<T>(
        EncryptedJsonPayload encryptedPayload,
        ReadOnlySpan<byte> dataEncryptionKey,
        EncryptionContext context,
        JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(encryptedPayload);
        ValidateKey(dataEncryptionKey, nameof(dataEncryptionKey));
        EncryptionContextValidator.Validate(context);
        ValidateEnvelope(
            encryptedPayload.Ciphertext,
            encryptedPayload.Nonce,
            encryptedPayload.Tag,
            nameof(encryptedPayload));
        EnsureKeyVersion(encryptedPayload.KeyVersion, context.KeyVersion, "JSON payload");

        var associatedData = AssociatedDataBuilder.Build(context);
        var plaintext = new byte[encryptedPayload.Ciphertext.Length];

        try
        {
            using var aes = new AesGcm(dataEncryptionKey, TagSizeInBytes);
            aes.Decrypt(
                encryptedPayload.Nonce,
                encryptedPayload.Ciphertext,
                encryptedPayload.Tag,
                plaintext,
                associatedData);
            return JsonSerializer.Deserialize<T>(plaintext, serializerOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, string parameterName)
    {
        if (key.Length != KeySizeInBytes)
            throw new ArgumentException("AES-256 keys must contain exactly 32 bytes.", parameterName);
    }

    private static void ValidateEnvelope(byte[]? ciphertext, byte[]? nonce, byte[]? tag, string parameterName)
    {
        if (ciphertext is null || ciphertext.Length == 0)
            throw new ArgumentException("Ciphertext must not be empty.", parameterName);
        if (nonce is null || nonce.Length != NonceSizeInBytes)
            throw new ArgumentException("AES-GCM nonces must contain exactly 12 bytes.", parameterName);
        if (tag is null || tag.Length != TagSizeInBytes)
            throw new ArgumentException("AES-GCM tags must contain exactly 16 bytes.", parameterName);
    }

    private static void EnsureKeyVersion(int envelopeVersion, int contextVersion, string envelopeName)
    {
        if (envelopeVersion != contextVersion)
            throw new CryptographicException($"The {envelopeName} key version does not match the encryption context.");
    }
}
