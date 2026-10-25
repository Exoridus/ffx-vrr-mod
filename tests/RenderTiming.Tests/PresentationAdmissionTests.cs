using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class PresentationAdmissionTests
{
    [Fact]
    public void First_refusal_remains_until_a_new_admission_instance()
    {
        var admission = new PresentationAdmission();
        Assert.Null(admission.Failure);
        Assert.True(admission.Refuse("unknown_producer"));
        Assert.False(admission.Refuse("later_failure"));
        Assert.Equal("unknown_producer", admission.Failure);
        Assert.Null(new PresentationAdmission().Failure);
    }

    [Fact]
    public void Concurrent_refusals_publish_one_stable_reason()
    {
        var admission = new PresentationAdmission();
        int first = 0;
        Parallel.For(0, 64, index =>
        {
            if (admission.Refuse($"producer_{index}")) Interlocked.Increment(ref first);
        });
        Assert.Equal(1, first);
        string? reason = admission.Failure;
        Assert.NotNull(reason);
        Assert.False(admission.Refuse("overwrite"));
        Assert.Equal(reason, admission.Failure);
    }
}

[Collection("Render scheduler")]
public sealed class LateAdmissionSchedulerTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.Advance = null;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Refusal_preserves_current_presentation_phase_then_closes_the_next_image()
    {
        var module = new VrrModule();
        module.PreparePresentation();
        module.RunClock();
        Assert.True(RenderPassContext.IsPresentation);
        module.RefusePresentation("unknown_producer");
        Assert.True(RenderPassContext.IsPresentation);
        Assert.Equal(0u, module.ReadExec());
        module.EndPass();
        Assert.False(RenderPassContext.IsPresentation);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
        RenderPassContext.ResetHistory();
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
    }

    [Fact]
    public void Refusal_after_image_admission_returns_to_native_at_the_clock_boundary()
    {
        var module = new VrrModule();
        module.PreparePass();
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
        module.RefusePresentation("unknown_producer");
        long serial = RenderPassContext.SimulationSerial;
        int calls = 0;
        NativeClock.Advance = () => calls++;
        module.RunNativePass();
        Assert.Equal(1, calls);
        Assert.Equal(serial + 1, RenderPassContext.SimulationSerial);
        Assert.False(RenderPassContext.IsActive);
        Assert.False(RenderPassContext.IsPresentation);
        Assert.True(module.CameraResets > 0);
        Assert.True(module.ActorResets > 0);
    }
}
