using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fahrenheit.Mods.Vrr;

internal sealed record FmvReplacementMetadata(int Width, int Height, double FrameRate, string VisualStatus, string RuntimeStatus)
{
    public string VideoCodec { get; init; } = "vp8";
    public IReadOnlyList<FmvAudioTrack> AudioTracks { get; init; } = [];

    public static FmvReplacementMetadata LoadValidated(string video)
        => LoadValidatedForLab(video, false);

    internal static FmvReplacementMetadata LoadValidatedForLab(string video, bool experimental)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(video + ".manifest.json"));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1
            || root.GetProperty("validation_status").GetString() != "automated_passed")
            throw new InvalidDataException("Movie manifest has no supported automated validation result.");

        using FileStream stream = File.OpenRead(video);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, root.GetProperty("output_sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Movie output hash does not match its manifest.");

        string? codec = ReadCodecId(video);
        bool av1 = experimental && codec == "V_AV1"
            && root.TryGetProperty("video_codec", out var declared) && declared.GetString() == "av1"
            && root.TryGetProperty("av1_decoder_validation", out var validation) && validation.GetString() == "fhav1-x86-v1"
            && root.TryGetProperty("av1_decoded_frame_count", out var decoded)
            && decoded.GetInt64() == root.GetProperty("frame_count").GetInt64();
        if (codec != "V_VP8" && !av1)
            throw new InvalidDataException("Movie requires VP8 or an explicitly validated AV1 lab candidate.");

        int width = root.GetProperty("width").GetInt32();
        int height = root.GetProperty("height").GetInt32();
        int numerator = root.GetProperty("frame_rate_numerator").GetInt32();
        int denominator = root.GetProperty("frame_rate_denominator").GetInt32();
        if (width <= 0 || width > (experimental ? 3840 : 1920) || height <= 0 || height > (experimental ? 2160 : 1080) || (width & 1) != 0 || (height & 1) != 0
            || numerator <= 0 || denominator <= 0 || root.GetProperty("frame_count").GetInt64() <= 0)
            throw new InvalidDataException("Movie dimensions, frame count or rate are unsupported.");

        double rate = (double)numerator / denominator;
        if (Math.Abs(rate - 30000d / 1001) > 1e-9 && Math.Abs(rate - 60000d / 1001) > 1e-9
            && (!experimental || Math.Abs(rate - 120000d / 1001) > 1e-9))
            throw new InvalidDataException(experimental
                ? "Lab movie frame rate must be 30000/1001, 60000/1001 or 120000/1001."
                : "Movie frame rate must be authored 30000/1001 or doubled 60000/1001.");
        return new(width, height, rate,
            root.GetProperty("visual_status").GetString() ?? "unreviewed",
            root.GetProperty("runtime_status").GetString() ?? "untested") { VideoCodec = av1 ? "av1" : "vp8" };
    }

    public static bool IsVp8(string video) => ReadCodecId(video) == "V_VP8";

    internal static string? ReadCodecId(string video)
    {
        using FileStream stream = File.OpenRead(video);
        byte[] header = new byte[65536];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 4 || header[0] != 0x1a || header[1] != 0x45 || header[2] != 0xdf || header[3] != 0xa3)
            return null;

        string? videoCodec = null;
        for (int i = 4; i + 2 < read; i++)
        {
            if (header[i] != 0x86) continue;
            int marker = 0x80;
            int bytes = 1;
            while (bytes <= 8 && (header[i + 1] & marker) == 0) { marker >>= 1; bytes++; }
            if (bytes > 8 || i + 1 + bytes >= read) continue;
            ulong length = (uint)(header[i + 1] & (marker - 1));
            for (int b = 1; b < bytes; b++) length = (length << 8) | header[i + 1 + b];
            int start = i + 1 + bytes;
            if (length is 0 or > 64 || (ulong)start + length > (ulong)read) continue;
            string codec = Encoding.ASCII.GetString(header, start, (int)length);
            if (!codec.StartsWith("V_", StringComparison.Ordinal)) continue;
            if (videoCodec is not null) return null;
            videoCodec = codec;
        }
        return videoCodec;
    }
}

internal sealed record FmvAudioTrack(int Number, string Codec, string Name, string Language);
