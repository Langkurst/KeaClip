using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Klippi.Infrastructure.Sharing;

/// <summary>One media fragment: where its <c>moof</c> box starts and its earliest decode time in seconds.</summary>
public sealed record Fmp4Fragment(double Time, long Offset);

/// <summary>
/// What the web player needs to stream a fragmented MP4 through Media Source Extensions: the init segment length
/// (<c>ftyp</c> + <c>moov</c>), the RFC 6381 codecs string for <c>addSourceBuffer</c>, and the fragment index used to seek.
/// </summary>
public sealed record Fmp4Layout(long Length, long InitLength, string Codecs, bool HasVideo, bool HasAudio, int? Width, int? Height, IReadOnlyList<Fmp4Fragment> Fragments)
{
    /// <summary>The end offset (exclusive) of fragment <paramref name="index"/>.</summary>
    public long FragmentEnd(int index) => index + 1 < Fragments.Count ? Fragments[index + 1].Offset : Length;
    /// <summary>The longest fragment in seconds; the last one lasts until <paramref name="totalSeconds"/>, the media duration.</summary>
    public double LongestFragmentSeconds(double totalSeconds)
        => Fragments.Zip(Fragments.Skip(1).Select(f => f.Time).Append(Math.Max(totalSeconds, Fragments[^1].Time)), (a, end) => end - a.Time).Max();
    public long LargestFragmentBytes => Fragments.Count == 0 ? 0 : Enumerable.Range(0, Fragments.Count).Max(i => FragmentEnd(i) - Fragments[i].Offset);
}

/// <summary>
/// Reads the box structure of a fragmented MP4 (as written by FFmpeg with <c>empty_moov</c>) without decoding media.
/// Only H.264 (<c>avc1</c>/<c>avc3</c>) and AAC/MP3 audio sample entries are accepted, because those are what browsers play.
/// </summary>
public static class Fmp4Index
{
    private const int MaximumBoxBytes = 64 * 1024 * 1024;

    /// <exception cref="InvalidDataException">The file is not a fragmented MP4 the web player can stream.</exception>
    public static Fmp4Layout Read(Stream stream)
    {
        // Malformed boxes that are too short for their fields surface as one kind of failure.
        try { return ReadCore(stream); }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException or EndOfStreamException or InvalidOperationException) { throw Invalid("A box is malformed."); }
    }

    private static Fmp4Layout ReadCore(Stream stream)
    {
        if (!stream.CanSeek) throw new ArgumentException("The stream must be seekable.", nameof(stream));
        long length = stream.Length, offset = 0, initLength = -1;
        Dictionary<uint, Track>? tracks = null;
        var fragments = new List<Fmp4Fragment>();
        bool first = true, expectMdat = false;
        while (offset < length)
        {
            var (type, size, header) = ReadHeader(stream, offset, length);
            if (first && type != "ftyp") throw Invalid("The file does not start with an ftyp box.");
            first = false;
            switch (type)
            {
                case "moov":
                    if (tracks is not null) throw Invalid("The file has more than one moov box.");
                    tracks = ParseMovie(ReadBody(stream, offset + header, size - header));
                    break;
                case "moof":
                    if (tracks is null) throw Invalid("A fragment comes before the movie header.");
                    if (expectMdat) throw Invalid("A fragment has no media data.");
                    if (initLength < 0) initLength = offset;
                    double time = FragmentTime(ReadBody(stream, offset + header, size - header), tracks);
                    if (fragments.Count > 0 && time + 0.001 < fragments[^1].Time) throw Invalid("Fragment times go backwards.");
                    fragments.Add(new Fmp4Fragment(time, offset));
                    expectMdat = true;
                    break;
                case "mdat":
                    if (initLength < 0) throw Invalid("Media data comes before the first fragment; the file is not fragmented.");
                    expectMdat = false;
                    break;
                case "mfra":
                    // The fragment random access box at the end belongs to no fragment; the player never fetches it.
                    if (offset + size != length) throw Invalid("The mfra box is not the last box.");
                    break;
            }
            offset += size;
        }
        if (tracks is null || fragments.Count == 0 || expectMdat) throw Invalid("The file is not a complete fragmented MP4.");
        var video = tracks.Values.FirstOrDefault(t => t.Kind == "vide");
        var audio = tracks.Values.FirstOrDefault(t => t.Kind == "soun");
        // The last fragment ends where the media ends: an mfra box after it is not part of any fragment.
        long mediaEnd = fragments.Count > 0 ? LastMediaEnd(stream, fragments[^1].Offset, length) : length;
        string codecs = string.Join(',', new[] { video?.Codec, audio?.Codec }.Where(c => c is not null));
        return new Fmp4Layout(mediaEnd, initLength, codecs, video is not null, audio is not null, video?.Width, video?.Height, fragments);
    }

    private static long LastMediaEnd(Stream stream, long lastFragment, long length)
    {
        long offset = lastFragment;
        while (offset < length)
        {
            var (type, size, _) = ReadHeader(stream, offset, length);
            if (type == "mfra") return offset;
            offset += size;
        }
        return length;
    }

    private sealed record Track(uint Id, string Kind, uint Timescale, string Codec, int? Width, int? Height);

    private static Dictionary<uint, Track> ParseMovie(byte[] moov)
    {
        var tracks = new Dictionary<uint, Track>();
        foreach (var (type, body) in Children(moov))
        {
            if (type != "trak") continue;
            uint id = 0, timescale = 0;
            string? kind = null, codec = null;
            int? width = null, height = null;
            foreach (var (childType, child) in Children(body))
            {
                if (childType == "tkhd") id = BinaryPrimitives.ReadUInt32BigEndian(child.AsSpan(child[0] == 1 ? 20 : 12, 4));
                if (childType != "mdia") continue;
                foreach (var (mediaType, media) in Children(child))
                {
                    if (mediaType == "mdhd") timescale = BinaryPrimitives.ReadUInt32BigEndian(media.AsSpan(media[0] == 1 ? 20 : 12, 4));
                    if (mediaType == "hdlr") kind = Ascii(media.AsSpan(8, 4));
                    if (mediaType != "minf") continue;
                    var stbl = Find(media, "stbl") ?? throw Invalid("A track has no sample table.");
                    var stsd = Find(stbl, "stsd") ?? throw Invalid("A track has no sample description.");
                    if (BinaryPrimitives.ReadUInt32BigEndian(stsd.AsSpan(4, 4)) != 1) throw Invalid("A track has several sample descriptions.");
                    (codec, width, height) = SampleEntry(stsd.AsSpan(8).ToArray());
                }
            }
            if (kind is not ("vide" or "soun")) continue;
            if (id == 0 || timescale == 0 || codec is null) throw Invalid("A track header is incomplete.");
            if ((kind == "vide") != codec.StartsWith("avc", StringComparison.Ordinal)) throw Invalid("A track's codec does not match its type.");
            if (tracks.Values.Any(t => t.Kind == kind)) throw Invalid("The player supports one video and one audio track.");
            tracks[id] = new Track(id, kind, timescale, codec, width, height);
        }
        if (tracks.Count == 0) throw Invalid("The file has no audio or video track.");
        return tracks;
    }

    private static (string Codec, int? Width, int? Height) SampleEntry(byte[] entries)
    {
        var (type, entry) = Children(entries).First();
        switch (type)
        {
            case "avc1" or "avc3":
                {
                    // VisualSampleEntry: 8 bytes of SampleEntry, then 70 bytes before the child boxes; width and height at 24 and 26.
                    int width = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(24, 2)), height = BinaryPrimitives.ReadUInt16BigEndian(entry.AsSpan(26, 2));
                    var avcC = Find(entry.AsSpan(78).ToArray(), "avcC") ?? throw Invalid("The H.264 track has no avcC box.");
                    if (avcC.Length < 4 || avcC[0] != 1) throw Invalid("The H.264 configuration is invalid.");
                    return (string.Create(CultureInfo.InvariantCulture, $"avc1.{avcC[1]:X2}{avcC[2]:X2}{avcC[3]:X2}"), width, height);
                }
            case "mp4a":
                {
                    // AudioSampleEntry: 8 bytes of SampleEntry, then 20 bytes before the child boxes.
                    var esds = Find(entry.AsSpan(28).ToArray(), "esds") ?? throw Invalid("The audio track has no esds box.");
                    return (AudioCodec(esds), null, null);
                }
            default:
                throw Invalid("The track codec is not H.264 or AAC.");
        }
    }

    // ES_Descriptor (tag 3) > DecoderConfigDescriptor (tag 4): objectTypeIndication, then DecoderSpecificInfo (tag 5) whose
    // first 5 bits are the AAC audio object type (31 escapes to 32 + the next 6 bits).
    private static string AudioCodec(byte[] esds)
    {
        int position = 4;
        if (ReadDescriptor(esds, ref position) != 3) throw Invalid("The audio descriptor is invalid.");
        position += 2;
        byte flags = esds[position++];
        if ((flags & 0x80) != 0) position += 2;
        if ((flags & 0x40) != 0) position += 1 + esds[position];
        if ((flags & 0x20) != 0) position += 2;
        if (ReadDescriptor(esds, ref position) != 4) throw Invalid("The audio decoder descriptor is invalid.");
        byte objectType = esds[position];
        if (objectType is 0x69 or 0x6B) return string.Create(CultureInfo.InvariantCulture, $"mp4a.{objectType:X2}");
        if (objectType != 0x40) throw Invalid("The audio codec is not AAC or MP3.");
        position += 13;
        if (ReadDescriptor(esds, ref position) != 5) throw Invalid("The AAC configuration is missing.");
        int audioObjectType = esds[position] >> 3;
        if (audioObjectType == 31) audioObjectType = 32 + (((esds[position] & 0x07) << 3) | (esds[position + 1] >> 5));
        return string.Create(CultureInfo.InvariantCulture, $"mp4a.40.{audioObjectType}");
    }

    private static int ReadDescriptor(byte[] data, ref int position)
    {
        if (position >= data.Length) throw Invalid("The audio descriptor is truncated.");
        int tag = data[position++];
        for (int i = 0; i < 4; i++)
        {
            if (position >= data.Length) throw Invalid("The audio descriptor is truncated.");
            if ((data[position++] & 0x80) == 0) break;
        }
        return tag;
    }

    // The earliest decode time over the fragment's track runs, from each traf's tfhd track id and tfdt base time.
    private static double FragmentTime(byte[] moof, Dictionary<uint, Track> tracks)
    {
        double? earliest = null;
        foreach (var (type, traf) in Children(moof))
        {
            if (type != "traf") continue;
            var tfhd = Find(traf, "tfhd") ?? throw Invalid("A fragment has no track header.");
            var tfdt = Find(traf, "tfdt") ?? throw Invalid("A fragment has no decode time.");
            uint id = BinaryPrimitives.ReadUInt32BigEndian(tfhd.AsSpan(4, 4));
            if (!tracks.TryGetValue(id, out var track)) continue;
            ulong decode = tfdt[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(tfdt.AsSpan(4, 8)) : BinaryPrimitives.ReadUInt32BigEndian(tfdt.AsSpan(4, 4));
            double seconds = decode / (double)track.Timescale;
            earliest = earliest is { } value ? Math.Min(value, seconds) : seconds;
        }
        return earliest ?? throw Invalid("A fragment has no known track.");
    }

    private static (string Type, long Size, int Header) ReadHeader(Stream stream, long offset, long length)
    {
        Span<byte> header = stackalloc byte[16];
        if (length - offset < 8) throw Invalid("The file ends inside a box header.");
        stream.Position = offset;
        stream.ReadExactly(header[..8]);
        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        string type = Ascii(header.Slice(4, 4));
        int headerLength = 8;
        if (size == 1)
        {
            if (length - offset < 16) throw Invalid("The file ends inside a box header.");
            stream.ReadExactly(header.Slice(8, 8));
            size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            headerLength = 16;
        }
        else if (size == 0) size = length - offset;
        if (size < headerLength || size > length - offset) throw Invalid("A box size is invalid.");
        return (type, size, headerLength);
    }

    private static byte[] ReadBody(Stream stream, long offset, long length)
    {
        if (length > MaximumBoxBytes) throw Invalid("A header box is too large.");
        byte[] body = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(body);
        return body;
    }

    private static IEnumerable<(string Type, byte[] Body)> Children(byte[] data)
    {
        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8) throw Invalid("A box is truncated.");
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Ascii(data.AsSpan(offset + 4, 4));
            int header = 8;
            if (size == 1)
            {
                if (data.Length - offset < 16) throw Invalid("A box is truncated.");
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                header = 16;
            }
            else if (size == 0) size = data.Length - offset;
            if (size < header || size > data.Length - offset) throw Invalid("A box size is invalid.");
            yield return (type, data.AsSpan(offset + header, (int)size - header).ToArray());
            offset += (int)size;
        }
    }

    private static byte[]? Find(byte[] data, string type) => Children(data).Where(c => c.Type == type).Select(c => c.Body).FirstOrDefault();
    private static string Ascii(ReadOnlySpan<byte> value) => Encoding.ASCII.GetString(value);
    private static InvalidDataException Invalid(string reason) => new(reason);
}
