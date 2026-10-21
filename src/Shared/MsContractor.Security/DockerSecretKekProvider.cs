using System.Security.Cryptography;
using System.Text;

namespace MsContractor.Security;

/// <summary>
/// Reads a Base64-encoded 32-byte KEK from a Docker secret file.
/// The file is read once when this provider is created; dispose the provider to clear its key material.
/// </summary>
public sealed class DockerSecretKekProvider : IDisposable
{
    private readonly byte[] _keyEncryptionKey;
    private bool _disposed;

    /// <summary>
    /// Creates a provider for a mounted Docker secret file.
    /// </summary>
    /// <param name="secretFilePath">Path to the mounted Base64 secret file.</param>
    /// <param name="keyVersion">The KEK version represented by this secret.</param>
    public DockerSecretKekProvider(string secretFilePath, int keyVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretFilePath);
        if (keyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(keyVersion), "The key version must be positive.");

        var encodedBytes = File.ReadAllBytes(secretFilePath);
        char[]? encodedCharacters = null;
        Span<byte> decodedKey = stackalloc byte[EnvelopeEncryption.KeySizeInBytes];
        decodedKey.Clear();
        try
        {
            encodedCharacters = Encoding.ASCII.GetChars(encodedBytes);
            var start = 0;
            var length = encodedCharacters.Length;
            while (start < length && char.IsWhiteSpace(encodedCharacters[start]))
                start++;
            while (length > start && char.IsWhiteSpace(encodedCharacters[length - 1]))
                length--;

            if (!Convert.TryFromBase64Chars(
                    encodedCharacters.AsSpan(start, length - start),
                    decodedKey,
                    out var bytesWritten) ||
                bytesWritten != EnvelopeEncryption.KeySizeInBytes)
            {
                throw new CryptographicException("Docker secret must contain a Base64-encoded 32-byte KEK.");
            }

            _keyEncryptionKey = decodedKey.ToArray();
            KeyVersion = keyVersion;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decodedKey);
            CryptographicOperations.ZeroMemory(encodedBytes);
            if (encodedCharacters is not null)
                Array.Clear(encodedCharacters);
        }
    }

    /// <summary>
    /// Gets the version associated with this secret.
    /// </summary>
    public int KeyVersion { get; }

    /// <summary>
    /// Provides read-only access to the KEK for an encryption operation.
    /// </summary>
    public ReadOnlySpan<byte> KeyMaterial
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _keyEncryptionKey;
        }
    }

    /// <summary>
    /// Clears the in-memory KEK.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        CryptographicOperations.ZeroMemory(_keyEncryptionKey);
        _disposed = true;
    }
}
