using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class PadPreparedTickTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.Advance = null;
        NativeClock.PadAction = null;
        NativeClock.LoopAction = null;
        VrrModule.ClearDiagnosticLoop();
        RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void CandidateIsSelectedBeforeNativeClockAndReportedOnlyAfterReturn(int port, bool capture)
    {
        var module = Prime(capture);
        module.SeedPadCandidate(1, port, 10.008, 10.01, 1);
        module.SeedPadCandidate(2, port, 10.04, 10.041, 2);
        module.DeadlineImage(10.05);
        PadTickCorrelation? inside = null;
        int calls = 0;
        NativeClock.Advance = () =>
        {
            calls++;
            inside = Prepared(module);
            Assert.NotNull(inside);
            Assert.Equal(1L, Assert.Single(inside.Observations).PollSequence);
            Assert.Equal(1L, inside.Latest[port]!.PollSequence);
            Assert.Null(inside.Latest[1 - port]);
            Assert.DoesNotContain(module.DiagnosticEvents, e => e.Kind == "render_pass");
        };
        module.DeadlineClockAndExit();
        Assert.Equal(1, calls);
        var result = LastCorrelation(module);
        Assert.Equal(inside!.Epoch, result.GetProperty("epoch").GetInt64());
        Assert.Equal(1L, Assert.Single(result.GetProperty("observations").EnumerateArray()).GetProperty("PollSequence").GetInt64());
        Assert.False(result.GetProperty("native_input_applied").GetBoolean());
        Assert.Null(Prepared(module));
    }

    [Fact]
    public void ZeroStepDoesNotSelectOrConsumeAnObservation()
    {
        var module = Prime(false);
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(10.005);
        NativeClock.Advance = () => Assert.Fail("Presentation must not call the native simulation clock");
        module.DeadlineClockWithoutExit();
        Assert.Null(Prepared(module));
        module.DeadlineExitOnly();
        Assert.Equal(JsonValueKind.Null, LastCorrelation(module).ValueKind);
        NativeClock.Advance = () => Assert.Equal(1L, Assert.Single(Prepared(module)!.Observations).PollSequence);
        module.DeadlineImage(10.02);
        module.DeadlineClockAndExit();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeTakeoverOrFailureDiscardsPreparedCandidates(bool throws)
    {
        var module = Prime(false);
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        module.DeadlineImage(10.05);
        var error = new InvalidOperationException("native fixture");
        NativeClock.Advance = () =>
        {
            Assert.NotNull(Prepared(module));
            if (throws) throw error;
            module.NativeMovie = true;
        };
        if (throws)
        {
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.DeadlineClockWithoutExit()));
            module.FailedPreparedTickExitForTest();
        }
        else module.DeadlineClockAndExit();
        Assert.Null(Prepared(module));
        if (!throws) Assert.Equal(JsonValueKind.Null, LastCorrelation(module).ValueKind);
    }

    [Fact]
    public void CandidateIsSelectedBeforeResolvingAnAlreadyDeferredLatch()
    {
        var module = Prime(false);
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        module.DeadlineImage(10.05);
        module.DeferPreparedTickLatchForTest();
        int calls = 0;
        NativeClock.PadAction = () =>
        {
            calls++;
            Assert.Equal(1L, Assert.Single(Prepared(module)!.Observations).PollSequence);
        };
        module.DeadlineClockAndExit();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void AnEpochBoundaryInsideNativeClockCannotReportThePreparedResult()
    {
        var module = Prime(false);
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        module.DeadlineImage(10.05);
        NativeClock.Advance = () =>
        {
            Assert.NotNull(Prepared(module));
            module.ReadPadCandidates(double.NaN);
        };
        module.DeadlineClockAndExit();
        Assert.Null(Prepared(module));
        Assert.Equal(JsonValueKind.Null, LastCorrelation(module).ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedReturnCannotRetainOrReportCandidatesAfterOuterFailure(bool capture)
    {
        var module = Prime(capture);
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        int loops = 0;
        var error = new InvalidOperationException("outer native fixture");
        NativeClock.LoopAction = _ =>
        {
            if (++loops == 1)
            {
                module.DeadlineImage(10.05);
                module.DeadlineClockWithoutExit();
            }
        };
        NativeClock.Advance = () =>
        {
            Assert.Equal(1L, Assert.Single(Prepared(module)!.Observations).PollSequence);
            module.CallDiagnosticLoop(1f / 60);
            throw error;
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        Assert.Equal(2, loops);
        Assert.Null(Prepared(module));
        Assert.All(module.DiagnosticEvents.Where(e => e.Kind == "render_pass"), e =>
            Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(e.Data).GetProperty("pad_tick_correlation").ValueKind));
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }

    private static VrrModule Prime(bool capture)
    {
        var module = new VrrModule().EnableAdapter();
        module.CaptureLoops(capture);
        module.DeadlineImage(10);
        module.DeadlineClockAndExit();
        module.DiagnosticEvents.Clear();
        return module;
    }

    private static PadTickCorrelation? Prepared(VrrModule module)
        => module.PreparedPadTickForTest;

    private static JsonElement LastCorrelation(VrrModule module)
        => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data).GetProperty("pad_tick_correlation");
}

public unsafe sealed partial class VrrModule
{
    internal PadTickCorrelation? PreparedPadTickForTest => _padPreparedTick;
    internal void FailedPreparedTickExitForTest() => render_scheduler_exit_loop(false);
    internal void DeferPreparedTickLatchForTest() { _render_clock_seen = false; h_pad_latch(); Assert.True(_pad_latch_pending); }
}
