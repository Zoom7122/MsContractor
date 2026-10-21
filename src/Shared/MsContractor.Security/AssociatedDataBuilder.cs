using System.Buffers.Binary;
using System.Text;

namespace MsContractor.Security;

/// <summary>
/// Builds the canonical associated data used by the security library.
/// </summary>
public static class AssociatedDataBuilder
{
    private static ReadOnlySpan<byte> Magic => "MCSC"u8;
    private const byte FormatVersion = 1;

    /// <summary>
    /// Encodes the context as a versioned, unambiguous byte sequence.
    /// String fields are UTF-8 and length-prefixed with unsigned 32-bit big-endian lengths.
    /// </summary>
    public static byte[] Build(EncryptionContext context)
    {
        EncryptionContextValidator.Validate(context);

        var accountId = Encoding.UTF8.GetBytes(context.AccountId.ToString("D").ToLowerInvariant());
        var recordId = Encoding.UTF8.GetBytes(context.RecordId);
        var purpose = Encoding.UTF8.GetBytes(context.Purpose);

        var totalLength = checked(
            Magic.Length + 1 +
            sizeof(uint) + accountId.Length +
            sizeof(uint) + recordId.Length +
            sizeof(uint) + purpose.Length +
            sizeof(int));
        var associatedData = new byte[totalLength];
        var offset = 0;

        Magic.CopyTo(associatedData.AsSpan(offset));
        offset += Magic.Length;
        associatedData[offset++] = FormatVersion;

        WriteLengthPrefixed(accountId, associatedData, ref offset);
        WriteLengthPrefixed(recordId, associatedData, ref offset);
        WriteLengthPrefixed(purpose, associatedData, ref offset);
        BinaryPrimitives.WriteInt32BigEndian(associatedData.AsSpan(offset, sizeof(int)), context.KeyVersion);

        return associatedData;
    }

    private static void WriteLengthPrefixed(byte[] value, byte[] destination, ref int offset)
    {
        BinaryPrimitives.WriteUInt32BigEndian(destination.AsSpan(offset, sizeof(uint)), checked((uint)value.Length));
        offset += sizeof(uint);
        value.CopyTo(destination.AsSpan(offset));
        offset += value.Length;
    }
}

internal static class EncryptionContextValidator
{
    public static void Validate(EncryptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.AccountId == Guid.Empty)
            throw new ArgumentException("The account identifier must not be empty.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.RecordId))
            throw new ArgumentException("The record identifier must not be empty.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.Purpose))
            throw new ArgumentException("The key purpose must not be empty.", nameof(context));
        if (context.KeyVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(context), "The key version must be positive.");
    }
}
