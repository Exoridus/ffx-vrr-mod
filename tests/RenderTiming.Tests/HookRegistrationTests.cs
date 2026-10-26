namespace Fahrenheit.Mods.Vrr;

using Xunit;

public sealed class HookRegistrationTests
{
    [Fact]
    public void Every_identity_has_an_explicit_descriptor()
    {
        foreach (var site in Enum.GetValues<HookSiteId>().Where(s => s != HookSiteId.Unknown))
        {
            var descriptor = Assert.IsType<HookDescriptor>(HookSites.Describe(site));
            Assert.Equal(site, descriptor.Site);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Group));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.ConfigPredicate));
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Effect));
        }
        Assert.Null(HookSites.Describe(HookSiteId.Unknown));
        Assert.Null(HookSites.Describe((HookSiteId)int.MaxValue));
        Assert.Equal(HookFamily.InputLatch, HookSites.Describe(HookSiteId.PadSnapshotProducer)!.Family);
        Assert.Equal(HookPurpose.IndependentPresentation, HookSites.Describe(HookSiteId.PadSnapshotProducer)!.Purpose);
        Assert.Contains("clock.effective_exec", HookSites.Describe(HookSiteId.PadSnapshotProducer)!.CoreContractIds);
    }

    [Fact]
    public void Unknown_selection_is_rejected_before_installation()
    {
        Assert.False(HookRegistrationPlan.TryCreate([new(HookSiteId.Unknown)], [], out var plan, out var error));
        Assert.Null(plan);
        Assert.Contains("Unknown", error);
    }

    [Fact]
    public void Disabled_selection_never_calls_installer()
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.ModuleFFXApplicationAnimate, false)], [], out var plan, out _));
        int calls = 0;
        Assert.False(plan!.Install(HookSiteId.ModuleFFXApplicationAnimate, () => { calls++; return true; }));
        Assert.Equal(0, calls);
        Assert.Equal(HookRegistrationState.Refused, plan.State);
    }

    [Fact]
    public void Missing_contract_is_rejected()
    {
        Assert.False(HookRegistrationPlan.TryCreate([new(HookSiteId.TimingVerticalBlankCounters)], [], out _, out var error));
        Assert.Contains("clock.simulation_step", error);
    }

    [Fact]
    public void Partial_required_group_is_rejected()
    {
        Assert.False(HookRegistrationPlan.TryCreate([new(HookSiteId.TimingATELWaitInit)], [], out _, out var error));
        Assert.Contains("native.atel_waits", error);
    }

    [Fact]
    public void Missing_required_group_is_rejected()
    {
        Assert.False(HookRegistrationPlan.TryCreate([], [], out _, out var error, ["native.atel_waits"]));
        Assert.Contains("native.atel_waits", error);
    }

    [Fact]
    public void Conflicting_clock_owners_are_rejected()
    {
        var selections = new[] { new HookSelection(HookSiteId.DiagnosticBindingsMainLoop),
            new HookSelection(HookSiteId.RenderSchedulerRenderMainLoop) };
        Assert.False(HookRegistrationPlan.TryCreate(selections, ["clock.pass_scope"], out _, out var error));
        Assert.Contains("owner", error);
    }

    [Fact]
    public void Missing_dependency_is_rejected()
    {
        Assert.False(HookRegistrationPlan.TryCreate([new(HookSiteId.CrossFadeSgDrawFilter)], [], out _, out var error));
        Assert.Contains(nameof(HookSiteId.TimingSgAccSetAlpha), error);
    }

    [Fact]
    public void Native_group_queues_completely_without_claiming_armed()
    {
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out var plan, out var error), error);
        Assert.Equal(HookRegistrationState.Preflighted, plan!.State);
        int calls = 0;
        Assert.True(plan.Install(HookSiteId.TimingATELWaitInit, () => { calls++; return true; }));
        Assert.Equal(HookRegistrationState.Queued, plan.State);
        Assert.False(plan.IsGroupComplete("native.atel_waits"));
        Assert.True(plan.Install(HookSiteId.TimingATELWaitExec, () => { calls++; return true; }));
        Assert.True(plan.Install(HookSiteId.TimingMovieFrameDelta, () => { calls++; return true; }));
        Assert.Equal(3, calls);
        Assert.True(plan.IsGroupComplete("native.atel_waits"));
        Assert.Equal(HookRegistrationState.GroupComplete, plan.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Installer_result_is_preserved_and_failure_latches(bool result)
    {
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out var plan, out _));
        Assert.Equal(result, plan!.Install(HookSiteId.TimingATELWaitInit, () => result));
        if (!result)
        {
            Assert.Equal(HookRegistrationState.Refused, plan.State);
            Assert.False(plan.Install(HookSiteId.TimingATELWaitExec, () => throw new Exception("must not call")));
        }
    }

    [Fact]
    public void Installer_exception_is_preserved_and_refuses_plan()
    {
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out var plan, out _));
        var expected = new InvalidOperationException("native registration failed");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            plan!.Install(HookSiteId.TimingATELWaitInit, () => throw expected)));
        Assert.Equal(HookRegistrationState.Refused, plan!.State);
    }

    [Fact]
    public void Unselected_duplicate_and_disposed_registrations_never_call_installer()
    {
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out var plan, out _));
        Assert.False(plan!.Install(HookSiteId.DxgiPresent, () => throw new Exception("must not call")));
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out plan, out _));
        Assert.True(plan!.Install(HookSiteId.TimingATELWaitInit, () => true));
        Assert.False(plan.Install(HookSiteId.TimingATELWaitInit, () => throw new Exception("must not call")));
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out plan, out _));
        plan!.Dispose();
        Assert.False(plan.Install(HookSiteId.TimingATELWaitInit, () => throw new Exception("must not call")));
        Assert.Equal(HookRegistrationState.Disposed, plan.State);
    }


    [Fact]
    public void Distinct_template_instances_queue_but_unbounded_expansion_never_completes()
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.AtelRateSettersAllLevelsTemplate)], [], out var plan, out _));
        Assert.True(HookSites.Describe(HookSiteId.AtelRateSettersAllLevelsTemplate)!.IsLoopTemplate);
        Assert.True(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 1, () => true));
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 1, () => true));
        Assert.Equal(HookRegistrationState.Queued, plan.State);
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.AtelRateSettersAllLevelsTemplate)));
    }

    [Fact]
    public void Declared_template_count_requires_every_concrete_instance()
    {
        Assert.True(HookRegistrationPlan.TryCreate(
            [new(HookSiteId.AtelRateSettersAllLevelsTemplate, ExpectedInstances: 2)], [], out var plan, out _));
        Assert.True(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 1, () => true));
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.AtelRateSettersAllLevelsTemplate)));
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 1, () => true));
        Assert.Equal(HookRegistrationState.GroupComplete, plan.State);
        Assert.False(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x300, 1,
            () => throw new Exception("excess expansion must not call")));
    }

    [Fact]
    public void Duplicate_instance_is_rejected_and_zero_target_template_is_rejected()
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.AtelRateSettersAllLevelsTemplate)], [], out var plan, out _));
        Assert.True(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 1, () => true));
        Assert.False(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 1,
            () => throw new Exception("duplicate must not call")));
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.AtelRateSettersAllLevelsTemplate)], [], out plan, out _));
        Assert.False(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, () => throw new Exception("template needs identity")));
    }

    [Fact]
    public void Negative_generation_and_invalid_expansion_count_are_rejected()
    {
        Assert.False(HookRegistrationPlan.TryCreate(
            [new(HookSiteId.AtelRateSettersAllLevelsTemplate, ExpectedInstances: 0)], [], out _, out _));
        Assert.True(HookRegistrationPlan.TryCreate(Waits(), [], out var plan, out _));
        Assert.False(plan!.Install(HookSiteId.TimingATELWaitInit, 0x100, -1, () => throw new Exception("invalid generation")));
    }


    [Fact]
    public void A_new_generation_refuses_the_existing_plan_before_registration()
    {
        Assert.True(HookRegistrationPlan.TryCreate(
            [new(HookSiteId.AtelRateSettersAllLevelsTemplate, ExpectedInstances: 2)], [], out var plan, out _));
        Assert.True(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 1, () => true));
        Assert.False(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 2,
            () => throw new Exception("generation change must not call")));
        Assert.Equal(HookRegistrationState.Refused, plan.State);
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.AtelRateSettersAllLevelsTemplate)));
    }

    private static HookSelection[] Waits() => [new(HookSiteId.TimingATELWaitInit),
        new(HookSiteId.TimingATELWaitExec), new(HookSiteId.TimingMovieFrameDelta)];
}
