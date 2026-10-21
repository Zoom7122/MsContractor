using System.Security.Cryptography;
using MsContractor.Security;

namespace MsContractor.Security.Tests;

public sealed class EnvelopeEncryptionTests
{
    private static readonly Guid AccountId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly EncryptionContext DataKeyContext = new(
        AccountId,
        "installation-42",
        "vendor.access_token",
        3);
    private static readonly EncryptionContext PayloadContext = new(
        AccountId,
        "installation-42",
        "vendor.access_token",
        1);

    [Fact]
    public void GenerateDataEncryptionKey_ReturnsRandom32ByteKeys()
    {
        var first = EnvelopeEncryption.GenerateDataEncryptionKey();
        var second = EnvelopeEncryption.GenerateDataEncryptionKey();

        Assert.Equal(32, first.Length);
        Assert.Equal(32, second.Length);
        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public void EncryptDataKey_UsesFreshNonceAndRoundTrips()
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var kek = EnvelopeEncryption.GenerateDataEncryptionKey();

        var first = EnvelopeEncryption.EncryptDataKey(dek, kek, DataKeyContext);
        var second = EnvelopeEncryption.EncryptDataKey(dek, kek, DataKeyContext);
        var decrypted = EnvelopeEncryption.DecryptDataKey(first, kek, DataKeyContext);

        Assert.Equal(dek, decrypted);
        Assert.Equal(3, first.KeyVersion);
        Assert.False(first.Nonce.AsSpan().SequenceEqual(second.Nonce));
        Assert.False(first.Ciphertext.AsSpan().SequenceEqual(second.Ciphertext));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("record")]
    [InlineData("purpose")]
    [InlineData("version")]
    public void DecryptDataKey_RejectsChangedAssociatedData(string changedField)
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var kek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var encrypted = EnvelopeEncryption.EncryptDataKey(dek, kek, DataKeyContext);
        var changedContext = ChangeContext(DataKeyContext, changedField);

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptDataKey(encrypted, kek, changedContext));
    }

    [Fact]
    public void DecryptDataKey_RejectsTamperedCiphertextNonceAndTag()
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var kek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var encrypted = EnvelopeEncryption.EncryptDataKey(dek, kek, DataKeyContext);
        var changedCiphertext = encrypted.Ciphertext.ToArray();
        changedCiphertext[0] ^= 0x01;
        var changedNonce = encrypted.Nonce.ToArray();
        changedNonce[0] ^= 0x01;
        var changedTag = encrypted.Tag.ToArray();
        changedTag[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptDataKey(encrypted with { Ciphertext = changedCiphertext }, kek, DataKeyContext));
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptDataKey(encrypted with { Nonce = changedNonce }, kek, DataKeyContext));
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptDataKey(encrypted with { Tag = changedTag }, kek, DataKeyContext));
    }

    [Fact]
    public void EncryptJson_RoundTripsPayloadAndUsesFreshNonce()
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var value = new TestPayload("customer-17", ["read", "write"]);

        var first = EnvelopeEncryption.EncryptJson(value, dek, PayloadContext);
        var second = EnvelopeEncryption.EncryptJson(value, dek, PayloadContext);
        var decrypted = EnvelopeEncryption.DecryptJson<TestPayload>(first, dek, PayloadContext);

        Assert.NotNull(decrypted);
        Assert.Equal(value.CustomerId, decrypted.CustomerId);
        Assert.Equal(value.Scopes, decrypted.Scopes);
        Assert.Equal(1, first.KeyVersion);
        Assert.False(first.Nonce.AsSpan().SequenceEqual(second.Nonce));
        Assert.False(first.Ciphertext.AsSpan().SequenceEqual(second.Ciphertext));
    }

    [Theory]
    [InlineData("account")]
    [InlineData("record")]
    [InlineData("purpose")]
    [InlineData("version")]
    public void DecryptJson_RejectsChangedAssociatedData(string changedField)
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var encrypted = EnvelopeEncryption.EncryptJson(new TestPayload("customer-17", []), dek, PayloadContext);
        var changedContext = ChangeContext(PayloadContext, changedField);

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptJson<TestPayload>(encrypted, dek, changedContext));
    }

    [Fact]
    public void DecryptJson_RejectsTamperedCiphertextNonceAndTag()
    {
        var dek = EnvelopeEncryption.GenerateDataEncryptionKey();
        var encrypted = EnvelopeEncryption.EncryptJson(new TestPayload("customer-17", []), dek, PayloadContext);
        var changedCiphertext = encrypted.Ciphertext.ToArray();
        changedCiphertext[0] ^= 0x01;
        var changedNonce = encrypted.Nonce.ToArray();
        changedNonce[0] ^= 0x01;
        var changedTag = encrypted.Tag.ToArray();
        changedTag[0] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptJson<TestPayload>(encrypted with { Ciphertext = changedCiphertext }, dek, PayloadContext));
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptJson<TestPayload>(encrypted with { Nonce = changedNonce }, dek, PayloadContext));
        Assert.ThrowsAny<CryptographicException>(() =>
            EnvelopeEncryption.DecryptJson<TestPayload>(encrypted with { Tag = changedTag }, dek, PayloadContext));
    }

    [Fact]
    public void EncryptDataKey_RejectsKeysThatAreNot32Bytes()
    {
        Assert.Throws<ArgumentException>(() =>
            EnvelopeEncryption.EncryptDataKey(new byte[31], new byte[32], DataKeyContext));
        Assert.Throws<ArgumentException>(() =>
            EnvelopeEncryption.EncryptDataKey(new byte[32], new byte[33], DataKeyContext));
    }

    private static EncryptionContext ChangeContext(EncryptionContext context, string field) => field switch
    {
        "account" => context with { AccountId = Guid.Parse("20000000-0000-0000-0000-000000000002") },
        "record" => context with { RecordId = "installation-43" },
        "purpose" => context with { Purpose = "vendor.subscription" },
        "version" => context with { KeyVersion = context.KeyVersion + 1 },
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private sealed record TestPayload(string CustomerId, string[] Scopes);
}
