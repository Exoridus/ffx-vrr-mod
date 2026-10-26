using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class FailedLoopCommitTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.LoopAction = null;
        NativeClock.Advance = null;
        VrrModule.ClearDiagnosticLoop();
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_simulation_does_not_commit_a_step_or_complete_snapshots(bool afterClock)
    {
        var module = Prime();
        module.PrepareCatchUp();
        ThrowFromLoop(module, afterClock);

        Assert.Equal((1L, 0L, 2), module.CompletionState);
        Assert.Equal(1, module.CameraCompletions);
        Assert.Equal(1, module.ActorCompletions);
    }

    [Fact]
    public void Failed_mapped_tick_cannot_publish_a_deadline_or_input_correlation()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(10.05);
        ThrowFromLoop(module, true);

        var pass = LastPass(module);
        Assert.Equal(JsonValueKind.Null, pass.GetProperty("tick_deadline_seconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, pass.GetProperty("pad_tick_correlation").ValueKind);
    }

    [Theory]
    [InlineData("before_clock")]
    [InlineData("simulation")]
    [InlineData("presentation")]
    [InlineData("capture_disabled")]
    public void Failed_loop_permanently_refuses_independent_admission(string phase)
    {
        var module = Prime();
        module.DeadlineImage(phase == "presentation" ? 10.005 : 10.05);
        if (phase == "capture_disabled") module.CaptureLoops(false);
        ThrowFromLoop(module, phase != "before_clock");

        Assert.Equal("native_loop_exception", module.InputGateReason);
        module.DeadlineReset();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(0, module.AdmittedImages);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal("native_loop_exception", module.InputGateReason);
        Assert.Equal(0, module.AdmittedImages);
    }

    [Fact]
    public void Failed_zero_step_presentation_does_not_commit_an_additional_image()
    {
        var module = Prime();
        module.DeadlineImage(10.005);
        NativeClock.LoopAction = _ =>
        {
            module.DeadlineClockWithoutExit();
            Assert.True(RenderPassContext.IsPresentation);
            throw new InvalidOperationException("native loop fixture");
        };
        Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60));

        Assert.Equal((1L, 0L, 0), module.CompletionState);
        Assert.Equal(1, module.CameraCompletions);
        Assert.Equal(1, module.ActorCompletions);
        Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("tick_deadline_seconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("pad_tick_correlation").ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_loop_commits_the_completed_simulation_or_presentation(bool presentation)
    {
        var module = Prime();
        module.DeadlineImage(presentation ? 10.005 : 10.05);
        NativeClock.LoopAction = _ => module.DeadlineClockWithoutExit();
        module.CallDiagnosticLoop(1f / 60);

        if (presentation)
        {
            Assert.Equal((1L, 1L, 0), module.CompletionState);
            Assert.Equal(1, module.CameraCompletions);
            Assert.Equal(1, module.ActorCompletions);
            Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("tick_deadline_seconds").ValueKind);
        }
        else
        {
            Assert.Equal((2L, 0L, 2), module.CompletionState);
            Assert.Equal(2, module.CameraCompletions);
            Assert.Equal(2, module.ActorCompletions);
            Assert.Equal(10.016666666666667, LastPass(module).GetProperty("tick_deadline_seconds").GetDouble(), 12);
        }
        Assert.Equal("", module.InputGateReason);
        Assert.True(LastLoopExit(module).GetProperty("native_normal_return").GetBoolean());
    }

    [Fact]
    public void Failed_loop_keeps_cleanup_and_propagates_the_original_exception()
    {
        var module = Prime();
        module.DeadlineImage(10.005);
        int cleanupCalls = 0;
        module.DiagnosticCleanup = () =>
        {
            Assert.True(RenderPassContext.IsPresentation);
            cleanupCalls++;
        };
        ThrowFromLoop(module, true);

        Assert.Equal(1, cleanupCalls);
        Assert.False(RenderPassContext.IsPresentation);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
        Assert.False(LastLoopExit(module).GetProperty("native_normal_return").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_normal_return_cannot_hide_the_outer_failure(bool capture)
    {
        var module = Prime();
        module.CaptureLoops(capture);
        module.DeadlineImage(10.05);
        int depth = 0;
        var error = new InvalidOperationException("outer native loop fixture");
        NativeClock.LoopAction = delta =>
        {
            if (depth++ == 0)
            {
                module.CallDiagnosticLoop(delta);
                throw error;
            }
            module.DeadlineClockWithoutExit();
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        Assert.Equal("native_loop_exception", module.InputGateReason);
        Assert.False(RenderPassContext.IsActive);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(0, module.AdmittedImages);
    }
    private static VrrModule Prime()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.DeadlineImage(10);
        NativeClock.LoopAction = _ => module.DeadlineClockWithoutExit();
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal((1L, 0L, 0), module.CompletionState);
        module.DiagnosticEvents.Clear();
        return module;
    }

    private static void ThrowFromLoop(VrrModule module, bool afterClock)
    {
        var error = new InvalidOperationException("native loop fixture");
        NativeClock.LoopAction = _ =>
        {
            if (afterClock) module.DeadlineClockWithoutExit();
            throw error;
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
    }

    private static JsonElement LastPass(VrrModule module)
        => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data);

    private static JsonElement LastLoopExit(VrrModule module)
        => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "loop_exit").Data);
}
