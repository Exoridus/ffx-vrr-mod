using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class DxgiPresentationTests
{
    [Fact]
    public void LegacySingleSampleCreationGetsACompleteFlipDescriptor()
    {
        var original = new PresentationDescriptor(1, 0, 2, 1, 0, 28, true);
        Assert.True(DxgiPresentationPolicy.TryCreate(original, true, out var flip, out _));
        Assert.Equal(new PresentationDescriptor(2, 3, 0x802, 1, 0, 28, true), flip);
    }

    [Theory]
    [InlineData(false, 1u, 0u, true, 28u)]
    [InlineData(true, 4u, 0u, true, 28u)]
    [InlineData(true, 1u, 1u, true, 28u)]
    [InlineData(true, 1u, 0u, false, 28u)]
    [InlineData(true, 1u, 0u, true, 29u)]
    public void UnsupportedCreationPreservesTheEngineDescriptor(bool supported, uint samples, uint quality, bool windowed, uint format)
    {
        var original = new PresentationDescriptor(1, 0, 2, samples, quality, format, windowed);
        Assert.False(DxgiPresentationPolicy.TryCreate(original, supported, out var result, out var reason));
        Assert.Equal(original, result);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData(0u, 0u, 0u, 0x802u)]
    [InlineData(1u, 2u, 2u, 0x802u)]
    [InlineData(4u, 0u, 4u, 0x802u)]
    public void ResizePreservesCreationFlagsAndFlipBufferCount(uint count, uint flags, uint wantedCount, uint wantedFlags)
    {
        var resize = DxgiPresentationPolicy.Resize(count, flags, 0x802);
        Assert.Equal(wantedCount, resize.BufferCount);
        Assert.Equal(wantedFlags, resize.Flags);
    }

    [Theory]
    [InlineData(true, 0u, 0u, 0x200u)]
    [InlineData(false, 0x200u, 0u, 0u)]
    [InlineData(true, 1u, 2u, 1u)]
    [InlineData(true, 8u, 0u, 0x208u)]
    public void TearingOnlyAccompaniesWindowedNonTestPresentation(bool windowed, uint flags, uint interval, uint wantedFlags)
    {
        var present = DxgiPresentationPolicy.Present(windowed, 2, flags);
        Assert.Equal(interval, present.Interval);
        Assert.Equal(wantedFlags, present.Flags);
    }

    [Fact]
    public void OcclusionTestAndDeviceLossDoNotCountAsSuccessfulPresentations()
    {
        var state = new PresentationLifecycle();
        state.CompletePresent(0, 0, 100);
        state.CompletePresent(0x087A0001, 0, 200);
        state.CompletePresent(0, 1, 300);
        state.CompletePresent(unchecked((int)0x887A0005), 0, 400);
        Assert.Equal(4, state.Attempts);
        Assert.Equal(1, state.Successes);
        Assert.Equal(100, state.LastSuccessTimestamp);
        Assert.False(state.Active);
    }

    [Fact]
    public void FailedPresentationStopsClockOwnershipExceptForNonblockingBackpressure()
    {
        var state = new PresentationLifecycle();
        state.CompletePresent(unchecked((int)0x887A000A), 0, 100);
        Assert.True(state.Active);
        Assert.Equal(0, state.Successes);
        state.CompletePresent(unchecked((int)0x887A0001), 0, 200);
        Assert.False(state.Active);
        state.CompletePresent(0, 0, 300);
        Assert.False(state.Active);
        Assert.Equal(1, state.Successes);
        Assert.Equal(300, state.LastSuccessTimestamp);
    }

    [Fact]
    public void ResizeFailureReleasesClockOwnershipWithoutInventingAPresentAttempt()
    {
        var state = new PresentationLifecycle();
        state.CompleteResize(0);
        Assert.True(state.Active);
        state.CompleteResize(unchecked((int)0x887A0001));
        Assert.False(state.Active);
        Assert.Equal(0, state.Attempts);
        Assert.Equal(0, state.Successes);
    }

    [Fact]
    public void PacingUsesAbsoluteDeadlinesAndDoesNotBurstAfterStalls()
    {
        var pacing = new PresentationPacer();
        Assert.Equal(1000, pacing.NextDeadline(1000, 120000, 120));
        Assert.Equal(2000, pacing.NextDeadline(1100, 120000, 120));
        Assert.Equal(3000, pacing.NextDeadline(2100, 120000, 120));
        Assert.Equal(9000, pacing.NextDeadline(9000, 120000, 120));
        Assert.Equal(10000, pacing.NextDeadline(9100, 120000, 120));
        Assert.Equal(9200, pacing.NextDeadline(9200, 120000, 60));
        Assert.Equal(11200, pacing.NextDeadline(9300, 120000, 60));
    }

    [Fact]
    public void Unlimited_pacing_returns_immediately_and_resumes_without_stale_debt()
    {
        var pacing = new PresentationPacer();
        Assert.Equal(1000, pacing.NextDeadline(1000, 120000, 144));
        Assert.Equal(1100, pacing.NextDeadline(1100, 120000, 0));
        Assert.Equal(1200, pacing.NextDeadline(1200, 120000, 0));
        Assert.Equal(1300, pacing.NextDeadline(1300, 120000, 144));
    }
    [Fact]
    public void OverlayBindingsCannotReplaceTheEngineTargetsRestoredAfterFlip()
    {
        var bindings = new PresentationBindings();
        bindings.Record([11, 12], 13, false, false);
        bindings.Record([21], 0, true, false);
        Assert.Equal(2u, bindings.Count);
        Assert.Equal((nint)11, bindings.Targets[0]);
        Assert.Equal((nint)12, bindings.Targets[1]);
        Assert.Equal((nint)13, bindings.Depth);
        bindings.Record([31], 33, false, true);
        Assert.Equal((nint)11, bindings.Targets[0]);
        bindings.Record([41], 0, false, false);
        Assert.Equal(1u, bindings.Count);
        Assert.Equal((nint)41, bindings.Targets[0]);
        Assert.Equal((nint)0, bindings.Targets[1]);
        Assert.Equal((nint)0, bindings.Depth);
        bindings.Clear();
        Assert.Equal(0u, bindings.Count);
        Assert.All(bindings.Targets, target => Assert.Equal((nint)0, target));
    }
}
