using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class NativeBuildTests
{
    [Fact]
    public void AcceptsExactExecutableIdentity()
    {
        Assert.True(NativeBuild.IsSupportedIdentity(10687744,
            "0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d"));
        Assert.True(NativeBuild.IsSupportedIdentity(NativeBuild.ImageLength, NativeBuild.ImageSha256.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(10687743, "0537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d")]
    [InlineData(10687744, "1537b2a1047f3266e73495cd4e35f63f0777f4231d417699f979954686da686d")]
    [InlineData(10687744, "")]
    public void RejectsUnknownExecutableIdentity(long length, string hash)
        => Assert.False(NativeBuild.IsSupportedIdentity(length, hash));

    [Fact]
    public void RejectsTestHostBeforeNativeIntegration()
    {
        Assert.False(NativeBuild.TryValidate(out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void UsesReviewedCurrentBuildBindings()
    {
        Assert.Equal((nint)0xEFBC34, EngineAddresses.SgKeepFps);
        Assert.Equal((nint)0xEFB7F0, EngineAddresses.SgVCount);
        Assert.Equal((nint)0xEFB7F4, EngineAddresses.SgVCount2);
        Assert.Equal((nint)0xEFB808, EngineAddresses.ElapsedFrameCountFrameSkip);
        Assert.Equal((nint)0xD334D4, EngineAddresses.PlayerChrs);
        Assert.Equal((nint)0x8DED3C, EngineAddresses.FmvPlayerManager);
        Assert.Equal((nint)0xF0070C, EngineAddresses.CrossFadeSlot0Counter);
        Assert.Equal((nint)0xF0070E, EngineAddresses.CrossFadeSlot0Alpha);
        Assert.Equal((nint)0x94E390, EngineAddresses.PpvPartManagers);
        Assert.Equal((nint)0x92C260, EngineAddresses.PpvFpManagers);
        Assert.Equal((nint)0xEFB8A0, EngineAddresses.IsNeedSync);
        Assert.Equal((nint)0xF3F77C, EngineAddresses.LimitTimerMode);
        Assert.Equal((nint)0xEFB7C0, EngineAddresses.EternalCalmCardFrames);
        Assert.Equal((nint)0x14716A4, EngineAddresses.EternalCalmCardAlpha);
        Assert.Equal((nint)0x2F520, EngineAddresses.FFXApplicationAnimate);
        Assert.Equal((nint)0x225090, EngineAddresses.SetFlipVSyncInterval);
        Assert.Equal((nint)0x421E80, EngineAddresses.AdvanceVBlankCounters);
        Assert.Equal((nint)0x21BF10, EngineAddresses.BigFileOpenFile);
        Assert.Equal((nint)0x4A68EB, EngineAddresses.BattleCursorBlinkGate);
        Assert.Equal((nint)0x23E2D3, EngineAddresses.DreamStarCounterCompare);
        Assert.Equal((nint)0x46CE55, EngineAddresses.AtelMoveLeadInLoad);
        Assert.Equal((nint)0x46D050, EngineAddresses.AtelInitMotionRecords);
        Assert.Equal((nint)0x45C4E0, EngineAddresses.AtelWaitInit);
        Assert.Equal((nint)0, EngineAddresses.MsSetChrStatInfo);
    }
}
