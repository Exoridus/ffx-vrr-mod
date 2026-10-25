using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class ReviewRegressionTests
{
    [Fact]
    public void InvalidOverridePreservesOtherSettingsWithoutReportingAReadFailure()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"PresentInterval\":0,\"TargetFramerateOverride\":144,\"Camera\":false}");
            var config = VrrConfig.Load(path, out var error, out var warning);
            Assert.Null(error);
            Assert.Contains("144", warning);
            Assert.Null(config.TargetFramerateOverride);
            Assert.Equal(0u, config.PresentInterval);
            Assert.False(config.Camera);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(30, 30d)]
    [InlineData(60, 60d)]
    [InlineData(120, 120d)]
    [InlineData(90, null)]
    [InlineData(144, null)]
    public void PanelRateUsesTheSameExplicitRatePolicy(int panelRate, double? expected)
    {
        var config = new VrrConfig();
        Assert.Equal(expected, config.ResolveTargetFramerate(panelRate, out var warning));
        Assert.Equal(panelRate != 0 && expected is null, warning is not null);
    }

    [Fact]
    public void ConfiguredOverrideTakesPrecedenceOverAnUnsupportedPanelRate()
    {
        var config = new VrrConfig { TargetFramerateOverride = 120 };
        Assert.Equal(120, config.ResolveTargetFramerate(144, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void MalformedConfigurationStillReportsReadFailureAndReturnsDefaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{");
            var config = VrrConfig.Load(path, out var error);
            Assert.NotNull(error);
            Assert.Equal(1u, config.PresentInterval);
            Assert.True(config.Camera);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(90d)]
    [InlineData(144d)]
    [InlineData(-60d)]
    [InlineData(double.NaN)]
    public void UnsupportedExplicitCorrectionCannotSeedTheStartupClock(double requested)
    {
        var method = typeof(VrrModule).GetMethod("initial_correction_rate", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(0f, method.Invoke(null, [1u, requested]));
    }

    [Fact]
    public void BadDreamCompareLogsOncePerRequestedThresholdAndNeverWrites()
    {
        var module = new VrrModule();
        VrrModule.TestRate = 60;
        NativeMemory.Clear(FhUtil.ptr_at<byte>(EngineAddresses.DreamStarCounterCompare), 4);
        var method = typeof(VrrModule).GetMethod("patch_dream_overlay_stars", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var logger = (Logger)typeof(VrrModule).GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module)!;
        for (int i = 0; i < 120; i++) method.Invoke(module, null);
        Assert.Single(logger.Errors);
        Assert.Empty(module._patches.Values);
        VrrModule.TestRate = 120;
        method.Invoke(module, null);
        method.Invoke(module, null);
        Assert.Equal(2, logger.Errors.Count);
        Assert.Empty(module._patches.Values);
    }

    [Theory]
    [InlineData(30d, 4004)]
    [InlineData(60d, 2002)]
    [InlineData(120d, 1001)]
    public void NtscMovieUsesTheNominalCorrectionClockWithoutPeriodicDroppedUploads(double correctionRate, int expected)
    {
        var method = typeof(VrrModule).GetMethod("advance_source_frame", BindingFlags.NonPublic | BindingFlags.Static)!;
        object[] args = [0d, 30000d / 1001, correctionRate];
        int uploads = 0;
        for (int i = 0; i < 4004; i++) if ((bool)method.Invoke(null, args)!) uploads++;
        Assert.Equal(expected, uploads);
    }

    [Fact]
    public void InitializationDoesNotOpenUnusedMovieFiles()
    {
        var module = new VrrModule();
        module._overrides["Video/unused.webm"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
        new byte[] { 0x8b, 0x41, 0x20, 0xc3, 0xcc }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x27b10), 5));
        new byte[] { 0x55, 0x8b, 0xec, 0x51, 0x57, 0x8b, 0xf9 }.CopyTo(new Span<byte>(FhUtil.ptr_at<byte>(0x628350), 7));
        var init = typeof(VrrModule).GetMethod("init_video_override", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)init.Invoke(module, null)!);
        Assert.Empty((HashSet<string>)typeof(VrrModule).GetField("_fmv_refused", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module)!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FirstSelectionVerifiesHashBeforeExposingReplacementMetadata(bool matchingHash)
    {
        string video = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".webm");
        try
        {
            byte[] bytes = new byte[] { 0x1a, 0x45, 0xdf, 0xa3, 0x86, 0x85 }.Concat(Encoding.ASCII.GetBytes("V_VP8")).ToArray();
            File.WriteAllBytes(video, bytes);
            File.WriteAllText(video + ".manifest.json", JsonSerializer.Serialize(new
            {
                schema_version = 1, validation_status = "automated_passed",
                output_sha256 = matchingHash ? Convert.ToHexString(SHA256.HashData(bytes)) : new string('0', 64),
                width = 1920, height = 1080, frame_rate_numerator = 60000,
                frame_rate_denominator = 1001, frame_count = 2,
                visual_status = "unreviewed", runtime_status = "untested"
            }));
            var module = new VrrModule();
            module._overrides["Video/test.webm"] = video;
            var select = typeof(VrrModule).GetMethod("try_select_fmv", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] args = ["Video/test.webm", null];
            Assert.Equal(matchingHash, (bool)select.Invoke(module, args)!);
            var metadata = (Dictionary<string, FmvReplacementMetadata?>)typeof(VrrModule).GetField("_fmv_metadata", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(module)!;
            if (matchingHash)
            {
                Assert.Equal(1920, metadata[video]!.Width);
                Assert.Equal(1080, metadata[video]!.Height);
                using var locked = File.Open(video, FileMode.Open, FileAccess.Read, FileShare.None);
                Assert.True((bool)select.Invoke(module, args)!);
            }
            else
            {
                Assert.Null(args[1]);
                Assert.False(metadata.ContainsKey(video));
            }
        }
        finally
        {
            File.Delete(video);
            File.Delete(video + ".manifest.json");
        }
    }
}
