using System.Reflection;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class NativeRenderValidationTests : IDisposable
{
    private readonly string? _request = Environment.GetEnvironmentVariable("FFX_VRR_RENDER_PROBE");
    public NativeRenderValidationTests() => Environment.SetEnvironmentVariable("FFX_VRR_RENDER_PROBE", "1");
    public void Dispose() => Environment.SetEnvironmentVariable("FFX_VRR_RENDER_PROBE", _request);

    [Fact]
    public void Explicit_reviewed_fixed60_validation_can_prepare_without_claiming_whole_game_qualification()
    {
        var module = new VrrModule { ChosenRate = 60 };
        module.ConfigureNativeValidationForTest();
        NativeClock.Hooks.Clear();
        Assert.True(module.Preflight());
        Assert.True(module.Effective);
        Assert.True(module.NativeValidationForTest);
        Assert.False(IndependentPresentationQualification.ReadEmbedded().Enabled);
        Assert.Empty(NativeClock.Hooks);
    }

    [Fact]
    public void Validation_frames_require_live_capture_but_normal_frames_do_not()
    {
        var module = new VrrModule { ChosenRate = 60 };
        module.ConfigureNativeValidationForTest();
        Assert.True(module.Preflight());
        module.ArmScheduler();
        module.CaptureLoops(true);
        Assert.True(module.NativeValidationFrameEligibleForTest());
        module.CaptureLoops(false);
        Assert.False(module.NativeValidationFrameEligibleForTest());
        Assert.Equal("native_validation_capture_inactive", module.InputGateReason);
        Environment.SetEnvironmentVariable("FFX_VRR_RENDER_PROBE", null);
        var normal = new VrrModule();
        normal.ArmScheduler();
        Assert.True(normal.NativeValidationFrameEligibleForTest());
    }
    [Fact]
    public void Validation_without_reviewed_source_resource_remains_refused()
    {
        Assert.False(new IndependentPresentationQualification("missing").AllowsNativeValidation("1", true, true, true, 60));
        Assert.False(new IndependentPresentationQualification("malformed", 14, 0).AllowsNativeValidation("1", true, true, true, 60));
    }
    [Fact]
    public void Scheduler_requires_the_prepared_static_vm_backend_before_installing_phase_hooks()
    {
        NativeClock.Hooks.Clear();
        var module = new VrrModule().EnableAdapter();
        module.StaticVmAvailableForTest = false;
        Assert.False(module.InitScheduler());
        Assert.Equal(1, module.StaticVmInitializationsForTest);
        Assert.Empty(NativeClock.Hooks);
    }
    [Theory]
    [InlineData("request")]
    [InlineData("phase")]
    [InlineData("diagnostics")]
    [InlineData("vrr")]
    [InlineData("rate30")]
    [InlineData("rate120")]
    public void Validation_requires_every_explicit_startup_boundary(string missing)
    {
        if (missing == "request") Environment.SetEnvironmentVariable("FFX_VRR_RENDER_PROBE", "true");
        var module = new VrrModule { ChosenRate = missing == "rate30" ? 30 : missing == "rate120" ? 120 : 60 };
        module.ConfigureNativeValidationForTest(missing);
        module.Preflight();
        Assert.False(module.NativeValidationForTest);
        Assert.False(module.Effective);
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool StaticVmAvailableForTest = true;
    internal int StaticVmInitializationsForTest;
    private bool init_static_effect_vm_clock() { StaticVmInitializationsForTest++; return _staticEffectVmClockInstalled = StaticVmAvailableForTest; }
    internal bool NativeValidationFrameEligibleForTest() => render_scheduler_frame_eligible();
    internal void ConfigureNativeValidationForTest(string? missing = null)
        => _config = _config with { VrrEnabled = missing != "vrr", NativePhaseProbe = missing != "phase", Diagnostics = missing != "diagnostics" };
    internal bool NativeValidationForTest => (bool)(typeof(VrrModule)
        .GetProperty("NativeRenderValidationEnabled", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(this) ?? false);
}
internal sealed partial record SchedulerConfig
{
    public bool Diagnostics { get; init; } = true;
}
