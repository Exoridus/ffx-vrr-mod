using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class FrameRateCounterTests
{
    [Fact]
    public void MeasuresRenderAndSimulationIndependently()
    {
        var counter = new FrameRateCounter();
        counter.Sample(10, 1000, 500);
        counter.Sample(10.5, 1060, 530);
        Assert.True(counter.HasSample);
        Assert.Equal(120, counter.Rendered);
        Assert.Equal(60, counter.Simulation);
    }

    [Fact]
    public void WaitsForHalfSecondAndKeepsLastReading()
    {
        var counter = new FrameRateCounter();
        counter.Sample(0, 0, 0);
        counter.Sample(0.25, 15, 15);
        Assert.False(counter.HasSample);
        counter.Sample(0.5, 30, 30);
        counter.Sample(0.6, 31, 30);
        Assert.Equal(60, counter.Rendered);
        Assert.Equal(60, counter.Simulation);
    }

    [Fact]
    public void StoppedSimulationReportsZeroInsteadOfTargetRate()
    {
        var counter = new FrameRateCounter();
        counter.Sample(0, 0, 10);
        counter.Sample(1, 60, 10);
        Assert.True(counter.HasSample);
        Assert.Equal(60, counter.Rendered);
        Assert.Equal(0, counter.Simulation);
    }
}
