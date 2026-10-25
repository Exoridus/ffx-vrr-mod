using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class FmvVideoLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fmv-library-" + Guid.NewGuid().ToString("N"));
    public FmvVideoLibraryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void MissingVideoUsesRetailAndLocalizedFilePrecedesSharedFile()
    {
        Assert.Null(FmvVideoLibrary.Find(_root, "../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm"));
        string shared = Path.Combine(_root, "opl.webm"), localized = Path.Combine(_root, "opl_us.webm");
        File.WriteAllText(shared, "fixture");
        Assert.Equal(shared, FmvVideoLibrary.Find(_root, "../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm"));
        File.WriteAllText(localized, "fixture");
        Assert.Equal(localized, FmvVideoLibrary.Find(_root, "../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm"));
        Assert.Equal(shared, FmvVideoLibrary.Find(_root, "../../../FFX_Data/GameData/PS3Data/Video/OPL_jp.webm"));
        Assert.Null(FmvVideoLibrary.Find(_root, "../../../FFX_Data/GameData/PS3Data/menu/opl_us.webm"));
    }

    [Fact]
    public void HeaderSuppliesDimensionsRateAndOriginalAudioTrackNumbersWithoutSidecar()
    {
        string file = Write(Video());
        var metadata = FmvVideoLibrary.ReadMetadata(file);
        Assert.Equal("av1", metadata.VideoCodec);
        Assert.Equal(3840, metadata.Width); Assert.Equal(2160, metadata.Height);
        Assert.Equal(60000d / 1001, metadata.FrameRate, 9);
        Assert.Equal(2, FmvVideoLibrary.SelectAudio(metadata, "opl_us.webm"));
        Assert.Equal(3, FmvVideoLibrary.SelectAudio(metadata, "opl_jp.webm"));
        Assert.Equal(2, metadata.AudioTracks.Count);
    }

    [Theory]
    [InlineData(3841, 2160, 16683333, "V_AV1")]
    [InlineData(3840, 2161, 16683333, "V_AV1")]
    [InlineData(7680, 4320, 16683333, "V_AV1")]
    [InlineData(3840, 2160, 8333333, "V_AV1")]
    [InlineData(3840, 2160, 0, "V_AV1")]
    [InlineData(3840, 2160, 16683333, "V_VP9")]
    public void UnsupportedCandidateIsRejectedBeforeNativeSelection(int width, int height, int duration, string codec)
        => Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ReadMetadata(Write(Video(width, height, duration, codec))));

    [Fact]
    public void DuplicateTrackNumbersAndTruncatedElementsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ReadMetadata(Write(Video(audioNumber: 1))));
        byte[] bytes = Video();
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ReadMetadata(Write(bytes[..^2])));
    }

    [Fact]
    public void AmbiguousLanguagesAreRejectedAndLocalizedSingleAudioNeedsNoTags()
    {
        var metadata = new FmvReplacementMetadata(1920, 1080, 60000d / 1001, "unreviewed", "untested")
        {
            AudioTracks = [new(2, "A_VORBIS", "", "und"), new(3, "A_VORBIS", "", "und")]
        };
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.SelectAudio(metadata, "opl_us.webm"));
        Assert.Equal(2, FmvVideoLibrary.SelectAudio(metadata with { AudioTracks = [metadata.AudioTracks[0]] }, "opl_us.webm"));
    }

    [Theory]
    [InlineData("opl_us.webm", 2)]
    [InlineData("opl_jp.webm", 3)]
    [InlineData("opl.webm", 2)]
    public void IsoLanguageControlsSelectionRegardlessOfDisplayTitle(string request, int expected)
    {
        var metadata = new FmvReplacementMetadata(1920, 1080, 60000d / 1001, "unreviewed", "untested")
        {
            AudioTracks = [new(2, "A_VORBIS", "opl_jp", "eng"), new(3, "A_VORBIS", "opl_us", "jpn")]
        };
        Assert.Equal(expected, FmvVideoLibrary.SelectAudio(metadata, request));
    }

    [Fact]
    public void MissingJapaneseTrackFallsBackToEnglish()
    {
        var metadata = new FmvReplacementMetadata(1920, 1080, 60000d / 1001, "unreviewed", "untested")
        {
            AudioTracks = [new(2, "A_VORBIS", "English", "eng"), new(3, "A_VORBIS", "French", "fra")]
        };
        Assert.Equal(2, FmvVideoLibrary.SelectAudio(metadata, "opl_jp.webm"));
    }

    [Fact]
    public void DuplicatePreferredLanguageIsRejectedEvenWithOneMatchingTitle()
    {
        var metadata = new FmvReplacementMetadata(1920, 1080, 60000d / 1001, "unreviewed", "untested")
        {
            AudioTracks = [new(2, "A_VORBIS", "opl_us", "eng"), new(3, "A_VORBIS", "Other English", "eng")]
        };
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.SelectAudio(metadata, "opl_us.webm"));
    }

    [Fact]
    public void LongInstalledPathUsesCheckedShortRelativePathWithinNativeBuffer()
    {
        string game = Path.Combine(_root, new string('a', 70));
        string file = Path.Combine(game, "fahrenheit", "mods", "fhvrr", "video", "opl.webm");
        string native = FmvVideoLibrary.ForNativePlayer(file, game);
        Assert.True(native.Length < 128);
        Assert.Equal(Path.GetFullPath(file), Path.GetFullPath(native, game));
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ForNativePlayer(Path.Combine(game, new string('b', 130) + ".webm"), game));
    }

    [Fact]
    public void MkvSelectionKeepsLocalizedPrecedenceAndExistingWebmPreference()
    {
        const string request = "../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm";
        string shared = Path.Combine(_root, "opl.mkv");
        File.WriteAllText(shared, "fixture");
        Assert.Equal(shared, FmvVideoLibrary.Find(_root, request));
        string sharedWebm = Path.Combine(_root, "opl.webm");
        File.WriteAllText(sharedWebm, "fixture");
        Assert.Equal(sharedWebm, FmvVideoLibrary.Find(_root, request));
        string localized = Path.Combine(_root, "opl_us.mkv");
        File.WriteAllText(localized, "fixture");
        Assert.Equal(localized, FmvVideoLibrary.Find(_root, request));
        string localizedWebm = Path.Combine(_root, "opl_us.webm");
        File.WriteAllText(localizedWebm, "fixture");
        Assert.Equal(localizedWebm, FmvVideoLibrary.Find(_root, request));
    }

    [Theory]
    [InlineData("webm")]
    [InlineData("matroska")]
    public void SupportedContainersShareMetadataAndOriginalAudioSelection(string docType)
    {
        var metadata = FmvVideoLibrary.ReadMetadata(Write(Video(docType: docType)));
        Assert.Equal(3840, metadata.Width);
        Assert.Equal(2, FmvVideoLibrary.SelectAudio(metadata, "OPL_us.webm"));
        Assert.Equal(3, FmvVideoLibrary.SelectAudio(metadata, "OPL_jp.webm"));
    }

    [Fact]
    public void MatroskaStillRejectsFlacAndUnknownDocumentTypes()
    {
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ReadMetadata(Write(Video(docType: "matroska", audioCodec: "A_FLAC"))));
        Assert.Throws<InvalidDataException>(() => FmvVideoLibrary.ReadMetadata(Write(Video(docType: "other"))));
    }

    private string Write(byte[] bytes)
    {
        string file = Path.Combine(_root, Guid.NewGuid() + ".webm");
        File.WriteAllBytes(file, bytes); return file;
    }

    internal static byte[] Video(int width = 3840, int height = 2160, int duration = 16683333,
        string codec = "V_AV1", int audioNumber = 2, string docType = "webm", string audioCodec = "A_VORBIS")
    {
        byte[] video = E(0xae, U(0xd7, 1), U(0x83, 1), S(0x86, codec), U(0x23e383, duration),
            E(0xe0, U(0xb0, width), U(0xba, height)));
        byte[] us = E(0xae, U(0xd7, audioNumber), U(0x83, 2), S(0x86, audioCodec), S(0x536e, "opl_us"), S(0x22b59c, "eng"));
        byte[] jp = E(0xae, U(0xd7, 3), U(0x83, 2), S(0x86, audioCodec), S(0x536e, "opl_jp"), S(0x22b59c, "jpn"));
        return E(0x1a45dfa3, S(0x4282, docType)).Concat(E(0x18538067, E(0x1654ae6b, video, us, jp))).ToArray();
    }
    private static byte[] S(uint id, string text) => E(id, Encoding.UTF8.GetBytes(text));
    private static byte[] U(uint id, int value)
    {
        byte[] data = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, (uint)value); return E(id, data);
    }
    private static byte[] E(uint id, params byte[][] children)
    {
        byte[] data = children.SelectMany(x => x).ToArray();
        var result = new List<byte>();
        for (int shift = 24; shift >= 0; shift -= 8)
            if (result.Count > 0 || (id >> shift) != 0) result.Add((byte)(id >> shift));
        int sizeBytes = data.Length < 127 ? 1 : 2;
        uint size = (uint)data.Length | (1u << (7 * sizeBytes));
        for (int shift = (sizeBytes - 1) * 8; shift >= 0; shift -= 8) result.Add((byte)(size >> shift));
        result.AddRange(data); return result.ToArray();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
