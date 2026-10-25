using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class TextureBlendProbeTests
{
    [Fact]
    public void TracksOnlyBoundedNonzeroResourcesAndDeduplicatesBindings()
    {
        var state = new TextureBlendProbeState();
        var source = new TextureBlendProbeSource(1, 2, 7, "w001", 3, 256, 256);
        Assert.False(state.Track(0, source));
        Assert.False(state.BeginDraw());
        Assert.True(state.Track(10, source));
        Assert.False(state.Track(10, source));
        Assert.False(state.Record(11, 0, 0, out _));
        Assert.True(state.Record(10, 0, 127, out var captured));
        Assert.Equal(source, captured);
        Assert.False(state.Record(10, 0, 127, out _));
        Assert.True(state.Record(10, 1, 127, out _));
        for (int i = 1; i < 32; i++) Assert.True(state.Track(10 + i, source));
        Assert.False(state.Track(42, source));
    }

    [Theory]
    [InlineData("w001", true)]
    [InlineData("WEp/W001.phyre", true)]
    [InlineData("menu/water_00", false)]
    [InlineData("w001_other", false)]
    public void OnlyTheRequestedModelConsumesTheProbeBudget(string name, bool expected)
        => Assert.Equal(expected, TextureBlendProbeState.IsTarget(name));

    [Fact]
    public void StopsScanningAndRecordingAtTheirIndependentLimits()
    {
        var draws = new TextureBlendProbeState();
        Assert.True(draws.Track(1, default));
        for (int i = 0; i < 4096; i++) Assert.True(draws.BeginDraw());
        Assert.False(draws.Active);
        Assert.False(draws.BeginDraw());
        var bindings = new TextureBlendProbeState();
        Assert.True(bindings.Track(1, default));
        for (uint i = 0; i < 64; i++) Assert.True(bindings.Record(1, 0, i, out _));
        Assert.False(bindings.Active);
        Assert.False(bindings.Record(1, 0, 64, out _));
        Assert.False(bindings.BeginDraw());
        bindings.Reset();
        Assert.True(bindings.Active);
        Assert.False(bindings.Record(1, 0, 0, out _));
        Assert.True(bindings.Track(1, default));
    }
}
