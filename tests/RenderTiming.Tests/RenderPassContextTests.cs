using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class RenderPassContextTests
{
    [Fact]
    public void ImageAndSimulationHaveIndependentSerials()
    {
        long image = RenderPassContext.ImageSerial;
        long simulation = RenderPassContext.SimulationSerial;
        RenderPassContext.BeginImage(1d / 144);
        Assert.Equal(image + 1, RenderPassContext.ImageSerial);
        Assert.Equal(simulation, RenderPassContext.SimulationSerial);
        RenderPassContext.BeginSimulation();
        RenderPassContext.BeginSimulation();
        Assert.Equal(image + 1, RenderPassContext.ImageSerial);
        Assert.Equal(simulation + 2, RenderPassContext.SimulationSerial);
    }

    [Fact]
    public void CatchUpPublishesOnlyItsLastPass()
    {
        var module = new VrrModule();
        module.PrepareCatchUp();
        long simulation = RenderPassContext.SimulationSerial;
        module.RunClock();
        Assert.False(RenderPassContext.IsFinalImage);
        Assert.Equal(simulation + 1, RenderPassContext.SimulationSerial);
        module.SetRemainingPasses(1);
        module.RunClock();
        Assert.True(RenderPassContext.IsFinalImage);
        Assert.Equal(0.5f, RenderPassContext.PresentationAlpha);
        Assert.Equal(0.5f, module.CameraAlpha);
        Assert.Equal(simulation + 2, RenderPassContext.SimulationSerial);
    }

    [Fact]
    public void ExtraImageDoesNotAdvanceSimulationIdentity()
    {
        var module = new VrrModule();
        module.PreparePresentation();
        long simulation = RenderPassContext.SimulationSerial;
        module.RunClock();
        Assert.True(RenderPassContext.IsFinalImage);
        Assert.Equal(simulation, RenderPassContext.SimulationSerial);
        RenderPassContext.BeginImage(1d / 144);
        Assert.False(RenderPassContext.IsFinalImage);
    }

    [Theory]
    [InlineData(float.NaN, 1)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void OutputAlphaStaysFiniteAndBounded(float value, float expected)
    {
        RenderPassContext.SetPass(value, true);
        Assert.Equal(expected, RenderPassContext.PresentationAlpha);
    }

    [Fact]
    public void NativeClockHandoffDisablesInterpolatedOutput()
    {
        var module = new VrrModule();
        module.PreparePass();
        NativeClock.Advance = () => module.NativeMovie = true;
        try
        {
            module.RunClock();
            Assert.False(RenderPassContext.IsFinalImage);
            Assert.Equal(1, RenderPassContext.PresentationAlpha);
        }
        finally { NativeClock.Advance = null; }
    }
}
