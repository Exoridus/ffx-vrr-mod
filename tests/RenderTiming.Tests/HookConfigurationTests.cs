namespace Fahrenheit.Mods.Vrr;

using Xunit;

public sealed class HookConfigurationTests
{
    private static VrrConfig NativeConfig => new() { AtelWorkerMotion = false, ParticleKernelHold = false };

    [Fact]
    public void Effective_native_mode_preserves_corrections_and_disables_independent_sites()
    {
        Assert.True(HookRegistrationPlan.TryCreateResolved(NativeConfig with { VrrEnabled = true },
            false, true, false, null, out var plan, out var error), error);
        Assert.True(plan!.IsSelected(HookSiteId.ModuleFFXApplicationAnimate));
        Assert.True(plan.IsSelected(HookSiteId.TimingVerticalBlankCounters));
        Assert.False(plan.IsSelected(HookSiteId.RenderSchedulerRenderMainLoop));
        Assert.False(plan.IsSelected(HookSiteId.PadSnapshotProducer));
        Assert.True(plan.IsSelected(HookSiteId.DiagnosticBindingsMainLoop));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Alpha_setter_is_selected_for_either_fade_policy(bool fades, bool hold)
    {
        Assert.True(HookRegistrationPlan.TryCreateResolved(NativeConfig with { Fades = fades, CrossFadeHold = hold },
            false, false, false, null, out var plan, out _));
        Assert.True(plan!.IsSelected(HookSiteId.TimingSgAccSetAlpha));
        Assert.Equal(hold, plan.IsSelected(HookSiteId.CrossFadeSgDrawFilter));
        Assert.Equal(fades, plan.IsSelected(HookSiteId.TimingSgFadeCommon));
    }

    [Fact]
    public void Motion_advance_selection_matches_all_three_enabling_flags()
    {
        foreach (bool survey in new[] { false, true })
        foreach (bool lend in new[] { false, true })
        foreach (bool hold in new[] { false, true })
        {
            var config = NativeConfig with { MotionSurvey = survey, MotionAdvanceLendFlag = lend, MotionHoldOptedOut = hold };
            Assert.True(HookRegistrationPlan.TryCreateResolved(config, false, false, false, null, out var plan, out _));
            Assert.Equal(survey || lend || hold, plan!.IsSelected(HookSiteId.MotionSurveyMotionAdvance));
        }
    }

    [Fact]
    public void Invalid_independent_configuration_is_rejected_before_any_installer()
    {
        Assert.False(HookRegistrationPlan.TryCreateResolved(NativeConfig with { VBlankRate = false },
            true, false, false, null, out var plan, out var error));
        Assert.Null(plan);
        Assert.Contains("VBlankRate", error);
    }

    [Fact]
    public void Config_selected_templates_require_the_actual_declared_target_inventory()
    {
        Assert.False(HookRegistrationPlan.TryCreateResolved(NativeConfig with { AtelWorkerMotion = true },
            false, false, false, null, out _, out var error));
        Assert.Contains("template", error);
    }

    [Fact]
    public void Diagnostic_and_native_optional_resources_are_not_falsely_complete()
    {
        Assert.True(HookRegistrationPlan.TryCreateResolved(NativeConfig with { AssetOverride = true, VideoOverride = true },
            false, true, false, null, out var plan, out _));
        Assert.True(plan!.IsSelected(HookSiteId.AssetOverrideBigFileStreamOpenFile));
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.AssetOverrideBigFileStreamOpenFile)));
        Assert.False(plan.Install(HookSiteId.DiagnosticBindingsMainLoop, 0x420ae0, 0, () => false));
        Assert.NotEqual(HookRegistrationState.Refused, plan.State);
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () => true));
    }

    [Fact]
    public void Declared_template_targets_reject_an_unexpected_target_before_the_installer()
    {
        Assert.True(HookRegistrationPlan.TryCreate(
            [new(HookSiteId.AtelRateSettersAllLevelsTemplate, ExpectedTargets: new nint[] { 0x100, 0x200 })],
            [], out var plan, out _));
        Assert.False(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x300, 0,
            () => throw new Exception("undeclared target must not install")));
    }
}
