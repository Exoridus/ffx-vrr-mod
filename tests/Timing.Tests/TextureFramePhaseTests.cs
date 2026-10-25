using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class TextureFramePhaseTests
{
    [Fact]
    public void Held_sixty_Hz_ticks_use_native_counter_and_fraction()
    {
        var phase = new TextureFramePhase();
        Assert.True(phase.Capture(1, 0, 0, 2, 1, 0.5));
        Assert.Equal(new TextureFrameBlend(0, 1, 0.25f), phase.Render(1, false));
        Assert.True(phase.Capture(2, 0, 1, 2, 1, 0));
        Assert.Equal(new TextureFrameBlend(0, 1, 0.5f), phase.Render(1, false));
        Assert.True(phase.Capture(3, 0, 1, 2, 1, 0.5));
        Assert.Equal(new TextureFrameBlend(0, 1, 0.75f), phase.Render(1, false));
    }

    [Fact]
    public void Last_frame_wraps_to_zero_without_reversing_phase()
    {
        var phase = new TextureFramePhase();
        Assert.True(phase.Capture(1, 7, 1, 2, 1, 0.5));
        Assert.Equal(new TextureFrameBlend(7, 0, 0.75f), phase.Render(1, false));
        Assert.True(phase.Capture(2, 0, 0, 2, 1, 0));
        Assert.Equal(new TextureFrameBlend(7, 0, 0.875f), phase.Render(0.5f, true));
    }

    [Fact]
    public void Paused_source_freezes_fraction_instead_of_following_global_hold_clock()
    {
        var phase = new TextureFramePhase();
        Assert.True(phase.Capture(1, 2, 1, 2, 1, 0.5));
        Assert.True(phase.Capture(2, 2, 1, 2, 0, 0));
        Assert.Equal(new TextureFrameBlend(2, 3, 0.75f), phase.Render(1, false));
        Assert.True(phase.Capture(3, 2, 1, 2, 0, 0.5));
        Assert.Equal(new TextureFrameBlend(2, 3, 0.75f), phase.Render(1, false));
    }

    [Theory]
    [InlineData(-1, 0, 2, 1)]
    [InlineData(8, 0, 2, 1)]
    [InlineData(0, -1, 2, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 2, -1)]
    [InlineData(0, 0, 2, 2)]
    public void Unsupported_phase_is_refused(int frame, int counter, int period, int step)
        => Assert.False(new TextureFramePhase().Capture(1, frame, counter, period, step, 0));

    [Fact]
    public void Catchup_repeated_capture_retains_adjacent_simulation_samples()
    {
        var phase = new TextureFramePhase();
        Assert.True(phase.Capture(1, 0, 0, 2, 1, 0));
        Assert.True(phase.Capture(2, 0, 1, 2, 1, 0));
        Assert.True(phase.Capture(2, 0, 1, 2, 1, 0.5));
        Assert.Equal(new TextureFrameBlend(0, 1, 0.375f), phase.Render(0.5f, true));
    }

    [Fact]
    public void Skipped_simulation_or_external_frame_change_does_not_bridge_history()
    {
        var phase = new TextureFramePhase();
        Assert.True(phase.Capture(1, 0, 0, 2, 1, 0));
        Assert.True(phase.Capture(3, 4, 1, 2, 1, 0));
        Assert.Equal(new TextureFrameBlend(4, 5, 0.5f), phase.Render(0.1f, true));
    }
}

public unsafe sealed partial class VrrModule { private double _hold_carry = 0; }
