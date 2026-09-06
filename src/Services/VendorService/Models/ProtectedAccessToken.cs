namespace MsContractor.VendorService.Models;

public sealed record ProtectedAccessToken(byte[] Ciphertext, byte[] Nonce, byte[] Tag, int KeyVersion);
