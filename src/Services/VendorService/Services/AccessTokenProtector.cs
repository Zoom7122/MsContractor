using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace MsContractor.VendorService.Services;

public sealed record ProtectedAccessToken(byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion);

public sealed class AccessTokenProtector
{
    private readonly byte[] _key;
    private readonly int _keyVersion;

    public AccessTokenProtector(IOptions<VendorOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.AccessTokenEncryptionKey);
        _keyVersion = options.Value.TokenKeyVersion;
    }

    public ProtectedAccessToken Protect(string accessToken)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = System.Text.Encoding.UTF8.GetBytes(accessToken);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintext);
        return new ProtectedAccessToken(ciphertext, nonce, tag, _keyVersion);
    }

    public string Unprotect(
        byte[] ciphertext,
        byte[] nonce,
        byte[] tag,
        int keyVersion)
    {
        if (keyVersion != _keyVersion)
            throw new CryptographicException("The access token key version is not available.");

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, tagSizeInBytes: 16);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return System.Text.Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
