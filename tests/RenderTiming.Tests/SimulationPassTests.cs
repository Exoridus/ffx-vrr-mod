using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class SimulationPassTests
{
    [Fact]
    public void CatchUpBatchAdvancesHoldAndSpawnClockOnEveryNativePass()
    {
        var module = new VrrModule();
        module.DisableScheduler();
        bool[] actual = Enumerable.Range(0, 6).Select(_ => module.RunNativePass()).ToArray();
        Assert.Equal(new[] { false, true, false, true, false, true }, actual);
        Assert.Equal(3, module.OverlayTicks);
        Assert.Equal(6, module.FrameSkipSamples);
    }

    [Fact]
    public void AdvancedSchedulerPassDoesNotAdvanceHoldClockTwice()
    {
        var module = new VrrModule();
        module.PreparePass();
        Assert.False(module.RunNativePass());
        Assert.Equal(0.5, module.HoldCarry);
        Assert.Equal(0, module.OverlayTicks);
        Assert.Equal(1, module.FrameSkipSamples);
    }

    [Fact]
    public void PresentationOnlyPassDoesNotAdvanceHoldOrSpawnClock()
    {
        var module = new VrrModule();
        module.PreparePresentation();
        module.RunNativePass();
        Assert.Equal(0, module.HoldCarry);
        Assert.Equal(0, module.OverlayTicks);
        Assert.Equal(0, module.FrameSkipSamples);
    }
}
