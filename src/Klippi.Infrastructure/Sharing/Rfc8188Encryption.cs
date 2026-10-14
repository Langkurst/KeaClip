using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Klippi.Infrastructure.Sharing;

/// <summary>RFC 8188 aes128gcm messages; crypto primitives come from the .NET runtime.</summary>
public static class Rfc8188Encryption
{
    public const int DefaultRecordSize = 65_536;
    private const int HeaderLength = 21;
    private const int TagLength = 16;
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> secretKey, int recordSize = DefaultRecordSize)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return EncryptWithSalt(plaintext, secretKey, salt, recordSize);
    }

    // Deterministic salt exists only for independent protocol fixtures. Production always creates a fresh salt.
    internal static byte[] EncryptWithSalt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> salt, int recordSize)
    {
        Validate(secretKey, recordSize);
        byte[] result = new byte[EncryptedLength(plaintext.Length, recordSize)];
        EncryptCore(plaintext, result, secretKey, salt, recordSize);
        return result;
    }

    /// <summary>Encrypts one message with a fresh salt into a caller-owned buffer, so chunk uploads can reuse one buffer instead of allocating per chunk and record.</summary>
    internal static int EncryptInto(ReadOnlySpan<byte> plaintext, Span<byte> destination, ReadOnlySpan<byte> secretKey, int recordSize = DefaultRecordSize)
    {
        Span<byte> salt = stackalloc byte[16];
        RandomNumberGenerator.Fill(salt);
        return EncryptCore(plaintext, destination, secretKey, salt, recordSize);
    }

    internal static int EncryptedLength(int plaintextLength, int recordSize = DefaultRecordSize)
    {
        if (plaintextLength < 0) throw new ArgumentOutOfRangeException(nameof(plaintextLength));
        if (recordSize is < 18 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(recordSize));
        int payloadSize = recordSize - TagLength - 1;
        int records = Math.Max(1, checked((int)(((long)plaintextLength + payloadSize - 1) / payloadSize)));
        return checked(HeaderLength + plaintextLength + records * (TagLength + 1));
    }

    private static int EncryptCore(ReadOnlySpan<byte> plaintext, Span<byte> destination, ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> salt, int recordSize)
    {
        Validate(secretKey, recordSize);
        if (salt.Length != 16) throw new ArgumentException("A 16-byte salt is required.", nameof(salt));
        int total = EncryptedLength(plaintext.Length, recordSize);
        if (destination.Length < total) throw new ArgumentException("The destination is too small for the encrypted message.", nameof(destination));
        if (plaintext.Overlaps(destination)) throw new ArgumentException("The plaintext and destination must not overlap.", nameof(destination));
        int payloadSize = recordSize - TagLength - 1;
        int records = Math.Max(1, (int)(((long)plaintext.Length + payloadSize - 1) / payloadSize));
        salt.CopyTo(destination);
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(16, 4), (uint)recordSize);
        destination[20] = 0;
        Span<byte> key = stackalloc byte[16], baseNonce = stackalloc byte[12], nonce = stackalloc byte[12];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, key, salt, KeyInfo);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, baseNonce, salt, NonceInfo);
        try
        {
            using var aes = new AesGcm(key, TagLength);
            int inputOffset = 0, outputOffset = HeaderLength;
            for (int record = 0; record < records; record++)
            {
                int length = Math.Min(payloadSize, plaintext.Length - inputOffset);
                // Each record is padded in place (payload + delimiter) and encrypted over itself; AES-GCM permits exactly overlapping buffers.
                Span<byte> body = destination.Slice(outputOffset, length + 1);
                plaintext.Slice(inputOffset, length).CopyTo(body);
                body[length] = record == records - 1 ? (byte)2 : (byte)1;
                Nonce(baseNonce, (ulong)record, nonce);
                aes.Encrypt(nonce, body, body, destination.Slice(outputOffset + body.Length, TagLength));
                inputOffset += length;
                outputOffset += body.Length + TagLength;
            }
            return total;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(baseNonce);
        }
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> secretKey)
    {
        if (ciphertext.Length < HeaderLength + TagLength + 1) throw new CryptographicException("Encrypted message is incomplete.");
        uint declaredSize = BinaryPrimitives.ReadUInt32BigEndian(ciphertext.Slice(16, 4));
        if (declaredSize > 16_777_216 || declaredSize < 18) throw new CryptographicException("Encrypted record size is invalid.");
        int recordSize = (int)declaredSize;
        Validate(secretKey, recordSize);
        int idLength = ciphertext[20];
        int offset = HeaderLength + idLength;
        if (offset > ciphertext.Length - TagLength - 1) throw new CryptographicException("Encrypted header is incomplete.");
        byte[] key = new byte[16], baseNonce = new byte[12];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, key, ciphertext[..16], KeyInfo);
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, baseNonce, ciphertext[..16], NonceInfo);
        using var output = new MemoryStream();
        try
        {
            using var aes = new AesGcm(key, TagLength);
            ulong record = 0;
            while (offset < ciphertext.Length)
            {
                int length = Math.Min(recordSize, ciphertext.Length - offset);
                if (length <= TagLength) throw new CryptographicException("Encrypted record is incomplete.");
                bool final = offset + length == ciphertext.Length;
                byte[] plain = new byte[length - TagLength];
                try
                {
                    aes.Decrypt(Nonce(baseNonce, record++), ciphertext.Slice(offset, plain.Length), ciphertext.Slice(offset + plain.Length, TagLength), plain);
                    int delimiter = plain.Length - 1;
                    while (delimiter >= 0 && plain[delimiter] == 0) delimiter--;
                    if (delimiter < 0 || plain[delimiter] != (final ? 2 : 1)) throw new CryptographicException("Encrypted record delimiter is invalid.");
                    output.Write(plain, 0, delimiter);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
                offset += length;
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(baseNonce);
        }
    }

    private static byte[] Nonce(ReadOnlySpan<byte> baseNonce, ulong sequence)
    {
        byte[] nonce = new byte[12];
        Nonce(baseNonce, sequence, nonce);
        return nonce;
    }

    private static void Nonce(ReadOnlySpan<byte> baseNonce, ulong sequence, Span<byte> nonce)
    {
        baseNonce.CopyTo(nonce);
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(nonce.Slice(4, 8));
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4, 8), value ^ sequence);
    }

    private static void Validate(ReadOnlySpan<byte> key, int recordSize)
    {
        if (key.Length != 16) throw new ArgumentException("A 16-byte secret key is required.", nameof(key));
        if (recordSize is < 18 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(recordSize));
    }
}
