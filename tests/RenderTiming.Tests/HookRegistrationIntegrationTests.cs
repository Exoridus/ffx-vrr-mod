namespace Fahrenheit.Mods.Vrr;

using Xunit;

[Collection("Render scheduler")]
public sealed class HookRegistrationIntegrationTests
{
    [Fact]
    public void Production_helpers_refuse_registration_without_an_owner_plan()
    {
        var module = new VrrModule();
        int calls = 0;
        Assert.False(module.RegisterOrdinaryForTest(HookSiteId.ModuleFFXApplicationAnimate, () => { calls++; return true; }));
        Assert.False(module.RegisterDiagnosticForTest(() => { calls++; return true; }));
        Assert.False(module.RegisterSurveyForTest(() => { calls++; return true; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Actual_preflight_rejects_invalid_config_before_the_first_installer()
    {
        var module = new VrrModule();
        Assert.False(module.PrepareRegistrationsForTest(new VrrConfig { VBlankRate = false }, true));
        Assert.False(module.RegisterOrdinaryForTest(HookSiteId.ModuleFFXApplicationAnimate,
            () => throw new Exception("invalid config must not install")));
    }

    [Fact]
    public void Actual_native_wrapper_completes_the_selected_wait_group()
    {
        var module = new VrrModule();
        Assert.True(module.PrepareRegistrationsForTest(NativeConfig));
        int calls = 0;
        foreach (var site in new[] { HookSiteId.TimingATELWaitInit, HookSiteId.TimingATELWaitExec, HookSiteId.TimingMovieFrameDelta })
            Assert.True(module.RegisterOrdinaryForTest(site, () => { calls++; return true; }));
        Assert.Equal(3, calls);
        Assert.True(module.RegistrationGroupForTest("native.atel_waits"));
        Assert.Equal(3, module.DiagnosticEvents.Count(e => e.Kind == "timing_hook"));
    }

    [Fact]
    public void Optional_diagnostic_failure_keeps_native_registration_available()
    {
        var module = new VrrModule();
        Assert.True(module.PrepareRegistrationsForTest(NativeConfig));
        Assert.False(module.RegisterDiagnosticForTest(() => throw new InvalidOperationException("probe failure")));
        Assert.True(module.RegisterOrdinaryForTest(HookSiteId.ModuleFFXApplicationAnimate, () => true));
        Assert.Contains(module.DiagnosticEvents, e => e.Kind == "probe_hook");
    }

    [Fact]
    public void Disabled_and_disposed_owner_plans_never_invoke_the_installer()
    {
        var disabled = new VrrModule();
        Assert.True(disabled.PrepareRegistrationsForTest(NativeConfig with { Camera = false }));
        Assert.False(disabled.RegisterOrdinaryForTest(HookSiteId.TimingMsCameraMoveFrame,
            () => throw new Exception("disabled site must not install")));
        var disposed = new VrrModule();
        Assert.True(disposed.PrepareRegistrationsForTest(NativeConfig));
        disposed.DisposeRegistrationsForTest();
        Assert.False(disposed.RegisterOrdinaryForTest(HookSiteId.ModuleFFXApplicationAnimate,
            () => throw new Exception("disposed owner must not install")));
    }


    [Fact]
    public void Default_native_config_queues_every_required_registration_with_bounded_templates()
    {
        var module = new VrrModule();
        var config = new VrrConfig();
        var targets = Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared)
            .Where(site => HookSites.Describe(site)!.IsLoopTemplate)
            .ToDictionary(site => site, _ => (IReadOnlyList<nint>)new nint[] { 0x1000, 0x2000 });
        Assert.True(module.PrepareRegistrationsForTest(config, false, targets));
        int calls = 0;
        foreach (var site in Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared))
        {
            HookSelection selection = HookRegistrationPlan.ResolveSelection(site, config, false, true, false, targets);
            if (!selection.Enabled || !selection.Required || selection.Deferred) continue;
            foreach (nint target in selection.ExpectedTargets ?? new nint[] { 0x3000 + (int)site })
                Assert.True(module.RegisterTargetForTest(site, target, () => { calls++; return true; }));
        }
        Assert.True(calls > 30);
        Assert.True(module.RequiredRegistrationsForTest);
        Assert.False(module.RegistrationGroupForTest(nameof(HookSiteId.DiagnosticBindingsMainLoop)));
    }

    private static VrrConfig NativeConfig => new() { AtelWorkerMotion = false, ParticleKernelHold = false };
}

public unsafe sealed partial class VrrModule
{
    internal bool PrepareRegistrationsForTest(VrrConfig config, bool independent = false,
        IReadOnlyDictionary<HookSiteId, IReadOnlyList<nint>>? targets = null)
        => prepare_hook_registration(config, independent, true, false, targets, probeRate: ChosenFramerate);
    internal bool RegisterOrdinaryForTest(HookSiteId site, Func<bool> install)
        => register_hook(site, site.ToString(), 0x100 + (int)site, install);
    internal bool RegisterDiagnosticForTest(Func<bool> install)
        => register_diagnostic_hook(HookSiteId.DiagnosticBindingsMainLoop, "main loop", 0x420ae0, true, install);
    internal bool RegisterSurveyForTest(Func<bool> install)
        => register_survey_hook(HookSiteId.SurveyChrTexanimDraw, "survey", 0x100, install);
    internal bool RegistrationGroupForTest(string group) => _hookRegistration?.IsGroupComplete(group) == true;
    internal bool RequiredRegistrationsForTest => _hookRegistration?.RequiredRegistrationsComplete == true;
    internal bool RegisterTargetForTest(HookSiteId site, nint target, Func<bool> install)
        => register_hook(site, site.ToString(), target, install);
    internal void DisposeRegistrationsForTest() => dispose_hook_registration();
}
