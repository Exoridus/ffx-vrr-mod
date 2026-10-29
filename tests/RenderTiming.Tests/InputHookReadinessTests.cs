using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class InputHookReadinessTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.Advance = null;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Incomplete_input_group_stays_native_before_image_admission(bool latch, bool readers)
    {
        var module = new VrrModule();
        Assert.True(module.EnableAdapter().InitScheduler());
        module.ArmScheduler();
        module.PrimeMappedClockForAdmission();
        module.InputReadiness(latch, readers);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.False(module.InputFrameEnabled);
        Assert.Equal(0, module.AdmittedImages);
        Assert.Equal(60, module.ReportedRate);
        Assert.Equal("pad_history_not_installed", module.InputGateReason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Lost_input_readiness_returns_to_native_before_the_clock(bool latch, bool readers)
    {
        var module = new VrrModule();
        module.PreparePass();
        module.InputReadiness(latch, readers);
        int calls = 0;
        NativeClock.Advance = () => calls++;
        module.RunNativePass();
        Assert.Equal(1, calls);
        Assert.False(module.InputFrameEnabled);
        Assert.False(RenderPassContext.IsActive);
        Assert.False(RenderPassContext.IsPresentation);
    }

    [Fact]
    public void Complete_input_group_preserves_other_image_admission_checks()
    {
        var module = new VrrModule();
        Assert.True(module.EnableAdapter().InitScheduler());
        module.ArmScheduler();
        module.PrimeMappedClockForAdmission();
        module.InputReadiness(true, true);
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
        Assert.True(module.InputFrameEnabled);
        Assert.Equal(1, module.AdmittedImages);
    }
}

public unsafe sealed partial class VrrModule
{
    internal void InputReadiness(bool latch, bool readers) { _pad_latch_installed = latch; _pad_history_readers_ready = readers; }
    internal bool InputFrameEnabled => _render_frame_enabled;
    internal string InputGateReason => _render_gate_reason();
}
