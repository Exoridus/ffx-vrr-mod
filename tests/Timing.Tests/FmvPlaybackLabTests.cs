namespace Fahrenheit.Mods.Vrr;

using System.Text.Json;
using Xunit;

public sealed class FmvPlaybackLabTests
{
    [Theory]
    [InlineData(1920, 1080, 60000, "V_VP8", false, true)]
    [InlineData(2560, 1440, 60000, "V_VP8", false, false)]
    [InlineData(2560, 1440, 60000, "V_VP8", true, true)]
    [InlineData(2560, 1440, 120000, "V_VP8", true, true)]
    [InlineData(2560, 1440, 120000, "V_VP9", true, false)]
    [InlineData(3840, 2160, 30000, "V_VP8", true, true)]
    [InlineData(3840, 2160, 60000, "V_VP8", true, true)]
    [InlineData(3840, 2160, 60000, "V_VP8", false, false)]
    [InlineData(7680, 4320, 30000, "V_VP8", true, false)]
    [InlineData(3840, 2160, 60000, "V_AV1", true, true)]
    [InlineData(1920, 1080, 60000, "V_AV1", false, false)]
    public void Experimental_formats_remain_explicit_VP8_and_bounded(int width, int height, int rate, string codec, bool experimental, bool accepted)
    {
        string video = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
        try
        {
            File.WriteAllBytes(video, new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x86, 0x85 }.Concat(System.Text.Encoding.ASCII.GetBytes(codec)).ToArray());
            File.WriteAllText(video + ".manifest.json", JsonSerializer.Serialize(new
            {
                schema_version = 1, output_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(video))),
                width, height, frame_rate_numerator = rate, frame_rate_denominator = 1001, frame_count = 20,
                validation_status = "automated_passed", visual_status = "unreviewed", runtime_status = "untested",
                video_codec = codec == "V_AV1" ? "av1" : "vp8", av1_decoder_validation = "fhav1-x86-v1", av1_decoded_frame_count = 20
            }));
            if (accepted) Assert.Equal(width, FmvReplacementMetadata.LoadValidatedForLab(video, experimental).Width);
            else Assert.Throws<InvalidDataException>(() => FmvReplacementMetadata.LoadValidatedForLab(video, experimental));
        }
        finally { File.Delete(video); File.Delete(video + ".manifest.json"); }
    }

    [Fact]
    public void Cases_are_stable_paths_and_only_replace_the_matching_movie()
    {
        string root = Path.Combine(Path.GetTempPath(), "fmv-lab-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "candidate.webm"), "fixture");
            string manifest = Path.Combine(root, "lab.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new[] { new { label = "1080p60", virtual_path = "ffx_data/gamedata/ps3data/video/opl_us.webm", file = "candidate.webm" } }));
            var lab = FmvPlaybackLab.Load(manifest);
            Assert.Null(lab.Select(0, "ffx_data/gamedata/ps3data/video/opl_us.webm"));
            Assert.Null(lab.Select(1, "ffx_data/gamedata/ps3data/video/opk_us.webm"));
            Assert.Equal(Path.Combine(root, "candidate.webm"), lab.Select(1, "FFX_Data/GameData/PS3Data/Video/OPL_us.webm"));
            Assert.Equal("Original (retail)", lab.Label(0));
            Assert.Equal("1080p60", lab.Label(1));
        }
        finally { Directory.Delete(root, true); }
    }
}
