using System.Buffers.Binary;
using System.Text;

namespace Fahrenheit.Mods.Vrr;

internal static class FmvVideoLibrary
{
    internal static string? Find(string directory, string request)
    {
        string path = request.Replace('\\', '/');
        if (path.StartsWith("../../../", StringComparison.Ordinal)) path = path[9..];
        path = path.TrimStart('/');
        const string prefix = "ffx_data/gamedata/ps3data/video/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string name = path[prefix.Length..].ToLowerInvariant();
        if (name.Contains('/') || !name.EndsWith(".webm", StringComparison.Ordinal)) return null;
        string stem = Path.GetFileNameWithoutExtension(name);
        string[] stems = stem.EndsWith("_us", StringComparison.Ordinal) || stem.EndsWith("_jp", StringComparison.Ordinal)
            ? [stem, stem[..^3]] : [stem];
        foreach (string candidateStem in stems)
        foreach (string extension in new[] { ".webm", ".mkv" })
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, candidateStem + extension));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    internal static string ForNativePlayer(string path, string currentDirectory)
    {
        string absolute = Path.GetFullPath(path).Replace('\\', '/');
        if (Fits(absolute)) return absolute;
        string relative = Path.GetRelativePath(currentDirectory, absolute).Replace('\\', '/');
        if (!Path.IsPathRooted(relative) && Fits(relative)
            && string.Equals(Path.GetFullPath(relative, currentDirectory), Path.GetFullPath(absolute), StringComparison.OrdinalIgnoreCase)) return relative;
        throw new InvalidDataException("Optional movie path does not fit the native 128-byte ASCII field.");
    }

    private static bool Fits(string path) => path.Length < 128 && path.All(c => c is > '\0' and <= '\x7f');

    internal static int? SelectAudio(FmvReplacementMetadata metadata, string request)
    {
        var tracks = metadata.AudioTracks;
        if (tracks.Count == 0) return null;
        if (tracks.Count == 1) return tracks[0].Number;
        string stem = Path.GetFileNameWithoutExtension(request.Replace('\\', '/'));
        bool jp = stem.EndsWith("_jp", StringComparison.OrdinalIgnoreCase);
        var language = tracks.Where(t => jp ? t.Language is "jpn" or "ja" or "ja-jp" : t.Language is "eng" or "en" or "en-us").ToArray();
        if (jp && language.Length == 0)
            language = tracks.Where(t => t.Language is "eng" or "en" or "en-us").ToArray();
        if (language.Length != 1) throw new InvalidDataException("Movie has no unique audio track for the requested language or English fallback.");
        return language[0].Number;
    }

    internal static FmvReplacementMetadata ReadMetadata(string video)
    {
        using FileStream stream = File.OpenRead(video);
        byte[] bytes = new byte[1024 * 1024];
        int count = stream.ReadAtLeast(bytes, bytes.Length, false);
        ReadOnlySpan<byte> header = bytes.AsSpan(0, count);
        int cursor = 0;
        var ebml = Next(header, ref cursor, count);
        if (ebml.Id != 0x1a45dfa3) throw new InvalidDataException("Optional movie has no EBML header.");
        int child = ebml.Start;
        string? docType = null;
        while (child < ebml.End)
        {
            var element = Next(header, ref child, ebml.End);
            if (element.Id == 0x4282) docType = Text(header, element);
        }
        if (docType is not ("webm" or "matroska")) throw new InvalidDataException("Optional movie must use WebM or Matroska.");
        var segment = Next(header, ref cursor, count);
        while (segment.Id == 0xec) segment = Next(header, ref cursor, count);
        if (segment.Id != 0x18538067) throw new InvalidDataException("Movie has no Matroska/WebM Segment.");
        cursor = segment.Start;
        while (cursor < segment.End)
        {
            var element = Next(header, ref cursor, segment.End);
            if (element.Id == 0x1654ae6b) return Tracks(header, element);
            if (element.Id == 0x1f43b675) break;
        }
        throw new InvalidDataException("Movie tracks must precede clusters within the bounded 1 MiB header.");
    }

    private static FmvReplacementMetadata Tracks(ReadOnlySpan<byte> bytes, Element tracks)
    {
        int cursor = tracks.Start, width = 0, height = 0, videos = 0;
        double declaredRate = 0;
        ulong defaultDuration = 0;
        var numbers = new HashSet<int>();
        var audio = new List<FmvAudioTrack>();
        while (cursor < tracks.End)
        {
            var entry = Next(bytes, ref cursor, tracks.End);
            if (entry.Id != 0xae) continue;
            int child = entry.Start, number = 0, type = 0;
            string codec = "", name = "", language = "und";
            ulong duration = 0;
            Element dimensions = default;
            while (child < entry.End)
            {
                var e = Next(bytes, ref child, entry.End);
                switch (e.Id)
                {
                    case 0xd7: number = CheckedInt(bytes, e); break;
                    case 0x83: type = CheckedInt(bytes, e); break;
                    case 0x86: codec = Text(bytes, e); break;
                    case 0x536e: name = Text(bytes, e); break;
                    case 0x22b59c: language = Text(bytes, e).ToLowerInvariant(); break;
                    case 0x22b59d: language = Text(bytes, e).ToLowerInvariant(); break;
                    case 0x23e383: duration = UInt(bytes, e); break;
                    case 0xe0: dimensions = e; break;
                }
            }
            if (number is < 1 or > 127 || !numbers.Add(number) || numbers.Count > 16)
                throw new InvalidDataException("Movie track numbers must be unique and between 1 and 127; at most 16 tracks are supported.");
            if (type == 1)
            {
                if (++videos != 1 || codec != "V_AV1") throw new InvalidDataException("Optional movie needs exactly one AV1 video track.");
                defaultDuration = duration;
                child = dimensions.Start;
                while (child < dimensions.End)
                {
                    var e = Next(bytes, ref child, dimensions.End);
                    if (e.Id == 0xb0) width = CheckedInt(bytes, e);
                    if (e.Id == 0xba) height = CheckedInt(bytes, e);
                    if (e.Id == 0x2383e3)
                    {
                        var value = bytes[e.Start..e.End];
                        declaredRate = value.Length == 8 ? BinaryPrimitives.ReadDoubleBigEndian(value)
                            : value.Length == 4 ? BinaryPrimitives.ReadSingleBigEndian(value) : 0;
                    }
                }
            }
            else if (type == 2)
            {
                if (codec != "A_VORBIS") throw new InvalidDataException("Optional movie audio must be original Vorbis, not FLAC or another codec.");
                audio.Add(new(number, codec, name, language));
            }
        }
        if (videos != 1 || width is < 2 or > 3840 || height is < 2 or > 2160 || (width & 1) != 0 || (height & 1) != 0)
            throw new InvalidDataException("Movie dimensions exceed the supported even 3840x2160 bounds.");
        double sourceRate = defaultDuration > 0 ? 1e9 / defaultDuration : declaredRate;
        double rate = new[] { 30000d / 1001, 60000d / 1001 }.FirstOrDefault(r => Math.Abs(sourceRate - r) < 0.001);
        if (rate == 0) throw new InvalidDataException("Optional movie needs a declared 29.97 or 59.94 frame rate.");
        return new(width, height, rate, "unreviewed", "untested") { VideoCodec = "av1", AudioTracks = audio.ToArray() };
    }

    private readonly record struct Element(uint Id, int Start, int End);
    private static Element Next(ReadOnlySpan<byte> bytes, ref int cursor, int end)
    {
        if (cursor >= end || bytes[cursor] == 0) throw new InvalidDataException("Truncated or invalid EBML element.");
        int idBytes = Length(bytes[cursor], 4);
        if (idBytes > end - cursor) throw new InvalidDataException("Truncated EBML id.");
        uint id = 0;
        for (int i = 0; i < idBytes; ++i) id = (id << 8) | bytes[cursor++];
        if (cursor >= end) throw new InvalidDataException("Missing EBML size.");
        int sizeBytes = Length(bytes[cursor], 8);
        if (sizeBytes > end - cursor) throw new InvalidDataException("Truncated EBML size.");
        ulong size = (uint)(bytes[cursor++] & (0xff >> sizeBytes));
        for (int i = 1; i < sizeBytes; ++i) size = (size << 8) | bytes[cursor++];
        bool unknown = size == ((1ul << (7 * sizeBytes)) - 1);
        int start = cursor;
        if (unknown || size > (ulong)(end - start))
        {
            if (id != 0x18538067) throw new InvalidDataException("EBML element exceeds the bounded header.");
            cursor = end;
        }
        else cursor += (int)size;
        return new(id, start, cursor);
    }
    private static int Length(byte value, int maximum)
    {
        int length = 1, marker = 0x80;
        while (length <= maximum && (value & marker) == 0) { length++; marker >>= 1; }
        return length <= maximum ? length : throw new InvalidDataException("Invalid EBML integer length.");
    }
    private static ulong UInt(ReadOnlySpan<byte> bytes, Element e)
    {
        if (e.End - e.Start is < 1 or > 8) throw new InvalidDataException("Invalid EBML unsigned integer.");
        ulong value = 0;
        for (int i = e.Start; i < e.End; ++i) value = (value << 8) | bytes[i];
        return value;
    }
    private static int CheckedInt(ReadOnlySpan<byte> bytes, Element e)
    {
        ulong value = UInt(bytes, e);
        return value <= int.MaxValue ? (int)value : throw new InvalidDataException("EBML integer exceeds native bounds.");
    }
    private static string Text(ReadOnlySpan<byte> bytes, Element e)
    {
        if (e.End - e.Start > 4096) throw new InvalidDataException("Movie track text exceeds its bounds.");
        return new UTF8Encoding(false, true).GetString(bytes[e.Start..e.End]);
    }
}
