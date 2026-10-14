using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Klippi.Infrastructure.Sharing;

/// <summary>
/// The seekable layout used by watch links: one RFC 8188 aes128gcm message over the whole file, with a fixed record size.
/// Every record except the last carries exactly <c>rs - 17</c> plaintext bytes (no padding), so any plaintext byte range
/// maps to whole records that a browser can fetch with one HTTP Range request and decrypt on their own. The last record
/// ends with delimiter 2, all others with 1, so truncation at a record boundary is detected. The message is standard RFC
/// 8188 and also decrypts with <see cref="Rfc8188Encryption.Decrypt"/>.
/// </summary>
public static class WatchLinkEncryption
{
    public const int RecordSize = 65_536;
    public const int HeaderLength = 21;
    /// <summary>Per-record overhead: the delimiter byte and the 16-byte GCM tag.</summary>
    public const int RecordOverhead = 17;

    public static int PayloadSize(int recordSize = RecordSize) => recordSize - RecordOverhead;

    public static long RecordCount(long plaintextLength, int recordSize = RecordSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(plaintextLength);
        Validate(recordSize);
        long payload = PayloadSize(recordSize);
        return Math.Max(1, (plaintextLength + payload - 1) / payload);
    }

    public static long EncryptedLength(long plaintextLength, int recordSize = RecordSize)
        => checked(HeaderLength + plaintextLength + RecordCount(plaintextLength, recordSize) * RecordOverhead);

    /// <summary>The ciphertext byte range (offset and length) holding the records that cover plaintext bytes [start, end).</summary>
    public static (long Offset, long Length) CipherRangeFor(long start, long end, long plaintextLength, int recordSize = RecordSize)
    {
        if (start < 0 || end <= start || end > plaintextLength) throw new ArgumentOutOfRangeException(nameof(end), "The range must be inside the plaintext.");
        long payload = PayloadSize(recordSize), first = start / payload, last = (end - 1) / payload;
        long offset = HeaderLength + first * recordSize;
        long stop = Math.Min(HeaderLength + (last + 1) * recordSize, EncryptedLength(plaintextLength, recordSize));
        return (offset, stop - offset);
    }

    internal static void Validate(int recordSize)
    {
        if (recordSize is < 18 or > 16_777_216) throw new ArgumentOutOfRangeException(nameof(recordSize));
    }
}

/// <summary>
/// Reads plaintext from a source stream and yields the seekable RFC 8188 ciphertext described by <see cref="WatchLinkEncryption"/>,
/// one record at a time, so a file of any size is encrypted with one record buffer. It also hashes the plaintext it read.
/// </summary>
internal sealed class WatchLinkEncryptingStream : Stream
{
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");
    private readonly Stream _source;
    private readonly long _plaintextLength, _records, _length;
    private readonly int _recordSize, _payloadSize;
    private readonly AesGcm _aes;
    private readonly byte[] _baseNonce = new byte[12], _nonce = new byte[12], _record;
    private readonly IncrementalHash _plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _nextRecord, _position;
    private int _pendingOffset, _pendingLength;
    private byte[]? _plaintextSha256;

    public WatchLinkEncryptingStream(Stream source, long plaintextLength, ReadOnlySpan<byte> secretKey, int recordSize = WatchLinkEncryption.RecordSize)
        : this(source, plaintextLength, secretKey, RandomNumberGenerator.GetBytes(16), recordSize) { }

    // A fixed salt exists only for interoperability fixtures (golden vectors). Production always uses a fresh random salt.
    internal WatchLinkEncryptingStream(Stream source, long plaintextLength, ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> salt, int recordSize)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (secretKey.Length != 16) throw new ArgumentException("A 16-byte secret key is required.", nameof(secretKey));
        if (salt.Length != 16) throw new ArgumentException("A 16-byte salt is required.", nameof(salt));
        WatchLinkEncryption.Validate(recordSize);
        _plaintextLength = plaintextLength;
        _recordSize = recordSize;
        _payloadSize = WatchLinkEncryption.PayloadSize(recordSize);
        _records = WatchLinkEncryption.RecordCount(plaintextLength, recordSize);
        _length = WatchLinkEncryption.EncryptedLength(plaintextLength, recordSize);
        _record = new byte[Math.Max(recordSize, WatchLinkEncryption.HeaderLength)];
        Span<byte> key = stackalloc byte[16];
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, key, salt, KeyInfo);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secretKey, _baseNonce, salt, NonceInfo);
            _aes = new AesGcm(key, 16);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        // The 21-byte header is the first pending output: salt, record size (big-endian) and an empty key id.
        salt.CopyTo(_record);
        BinaryPrimitives.WriteUInt32BigEndian(_record.AsSpan(16, 4), (uint)recordSize);
        _record[20] = 0;
        _pendingLength = WatchLinkEncryption.HeaderLength;
    }

    /// <summary>SHA-256 of the plaintext, available once the whole ciphertext has been read.</summary>
    public byte[] PlaintextSha256 => _plaintextSha256 ?? throw new InvalidOperationException("The whole message has not been read yet.");
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (_pendingLength == 0)
        {
            // With sync: true the fill never awaits, so it has always completed here; GetResult rethrows its failure.
            var fill = FillRecord(sync: true);
            if (!fill.IsCompleted) throw new InvalidOperationException("A synchronous read did not complete synchronously.");
            fill.GetAwaiter().GetResult();
        }
        return Copy(buffer);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pendingLength == 0) await FillRecord(sync: false, cancellationToken).ConfigureAwait(false);
        return Copy(buffer.Span);
    }

    private int Copy(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, _pendingLength);
        _record.AsSpan(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count; _pendingLength -= count; _position += count;
        return count;
    }

    // Encrypts the next record in place: payload, delimiter (1, or 2 for the last record), then the tag.
    private async ValueTask FillRecord(bool sync, CancellationToken cancellationToken = default)
    {
        if (_nextRecord >= _records) return;
        long index = _nextRecord;
        bool final = index == _records - 1;
        int length = (int)Math.Min(_payloadSize, _plaintextLength - index * _payloadSize);
        Memory<byte> payload = _record.AsMemory(0, length);
        if (sync) _source.ReadExactly(payload.Span);
        else await _source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        _plaintextHash.AppendData(payload.Span);
        _record[length] = final ? (byte)2 : (byte)1;
        _baseNonce.CopyTo(_nonce, 0);
        ulong counter = BinaryPrimitives.ReadUInt64BigEndian(_nonce.AsSpan(4, 8)) ^ (ulong)index;
        BinaryPrimitives.WriteUInt64BigEndian(_nonce.AsSpan(4, 8), counter);
        Span<byte> body = _record.AsSpan(0, length + 1);
        _aes.Encrypt(_nonce, body, body, _record.AsSpan(length + 1, 16));
        _nextRecord++;
        (_pendingOffset, _pendingLength) = (0, length + WatchLinkEncryption.RecordOverhead);
        if (final)
        {
            // The source must end exactly where its declared length ends; a longer file means it changed.
            Memory<byte> probe = new byte[1];
            int extra = sync ? _source.Read(probe.Span) : await _source.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
            if (extra != 0) throw new IOException("The source is longer than its declared length.");
            _plaintextSha256 = _plaintextHash.GetHashAndReset();
        }
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _aes.Dispose();
            _plaintextHash.Dispose();
            CryptographicOperations.ZeroMemory(_baseNonce);
            CryptographicOperations.ZeroMemory(_nonce);
            CryptographicOperations.ZeroMemory(_record);
        }
        base.Dispose(disposing);
    }
}
