namespace Fahrenheit.Mods.Vrr;

using Xunit;

public sealed class HookContinuationTests
{
    [Fact]
    public void Queue_success_without_a_receipt_is_not_verified_continuation_evidence()
    {
        var plan = NativePlan();
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () => true));
        Assert.True(plan.RequiredRegistrationsComplete);
        Assert.False(plan.TryVerifyRequiredContinuations(out string? reason));
        Assert.Contains("continuation", reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HookRegistrationState.GroupComplete, plan.State);
    }

    [Fact]
    public void A_receipt_is_probed_only_after_queueing_and_does_not_publish_an_arm_state()
    {
        var plan = NativePlan();
        Action callback = First;
        int probes = 0;
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation(callback, () => { probes++; return true; })));
        Assert.Equal(0, probes);
        Assert.True(plan.TryVerifyRequiredContinuations(out string? reason), reason);
        Assert.Null(reason);
        Assert.Equal(1, probes);
        Assert.Equal(HookRegistrationState.GroupComplete, plan.State);
    }

    [Fact]
    public void The_required_queue_must_be_complete_before_any_probe_runs()
    {
        var plan = NativePlan(new HookSelection(HookSiteId.ModuleSetFlipVSyncInterval));
        int probes = 0;
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => { probes++; return true; })));
        Assert.False(plan.TryVerifyRequiredContinuations(out string? reason));
        Assert.Contains("registration", reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void False_or_throwing_probes_fail_closed(bool throws)
    {
        var plan = NativePlan();
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => throws
                ? throw new InvalidOperationException("continuation unavailable") : false)));
        Assert.False(plan.TryVerifyRequiredContinuations(out string? reason));
        Assert.Contains(nameof(HookSiteId.ModuleFFXApplicationAnimate), reason!);
        Assert.Equal(HookRegistrationState.GroupComplete, plan.State);
    }

    [Fact]
    public void Each_verification_rechecks_the_probe_instead_of_latching_a_positive_result()
    {
        var plan = NativePlan();
        bool available = false;
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => available)));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
        available = true;
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        available = false;
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void A_receipt_cannot_be_added_outside_its_installer_attempt()
    {
        var plan = NativePlan();
        Assert.False(plan.RecordContinuation((Action)First, () => true));
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () => true));
        Assert.False(plan.RecordContinuation((Action)First, () => true));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void A_different_thread_cannot_attach_evidence_to_the_active_attempt()
    {
        var plan = NativePlan();
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
        {
            var foreign = Task.Run(() => plan.RecordContinuation((Action)Second, () => true));
            Assert.True(foreign.Wait(TimeSpan.FromSeconds(2)), "Foreign evidence must be rejected without waiting on the installer lock.");
            Assert.False(foreign.Result);
            return plan.RecordContinuation((Action)First, () => true);
        }));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void One_attempt_cannot_claim_two_callbacks_before_native_insertion()
    {
        var plan = NativePlan();
        int nativeInsertions = 0;
        Assert.False(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
            plan.RecordContinuation((Action)First, () => true)
            && plan.RecordContinuation((Action)Second, () => true)
            && ++nativeInsertions > 0));
        Assert.Equal(0, nativeInsertions);
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Delegate_identity_collisions_are_refused_before_another_native_insertion(bool equalSeparateInstance)
    {
        var plan = TemplatePlan();
        Action first = new(First);
        Action repeated = equalSeparateInstance ? new Action(First) : first;
        Assert.Equal(first, repeated);
        if (equalSeparateInstance) Assert.NotSame(first, repeated);
        int nativeInsertions = 0;
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 0, () =>
            plan.RecordContinuation(first, () => true) && ++nativeInsertions > 0));
        Assert.False(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 0, () =>
            plan.RecordContinuation(repeated, () => true) && ++nativeInsertions > 0));
        Assert.Equal(1, nativeInsertions);
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void Callback_reuse_is_also_rejected_across_different_sites()
    {
        var plan = NativePlan(new HookSelection(HookSiteId.ModuleSetFlipVSyncInterval));
        Action callback = First;
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation(callback, () => true)));
        Assert.False(plan.Install(HookSiteId.ModuleSetFlipVSyncInterval, 0x200, 0,
            () => plan.RecordContinuation(callback, () => true)));
    }

    [Fact]
    public void Every_concrete_template_instance_requires_its_own_receipt()
    {
        var plan = TemplatePlan();
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => true)));
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 0, () => true));
        Assert.True(plan.RequiredRegistrationsComplete);
        Assert.False(plan.TryVerifyRequiredContinuations(out string? reason));
        Assert.Contains("200", reason!);
    }

    [Fact]
    public void Distinct_template_callbacks_can_verify_their_bounded_queue()
    {
        var plan = TemplatePlan();
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 4,
            () => plan.RecordContinuation((Action)First, () => true)));
        Assert.True(plan.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x200, 4,
            () => plan.RecordContinuation((Action)Second, () => true)));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_optional_attempts_discard_receipts_and_preserve_installer_exceptions(bool throws)
    {
        var plan = NativePlan(new HookSelection(HookSiteId.ModuleSetFlipVSyncInterval, Required: false));
        Action callback = First;
        int staleProbes = 0;
        var expected = new InvalidOperationException("queue rejected");
        bool Attempt() => plan.Install(HookSiteId.ModuleSetFlipVSyncInterval, 0x200, 0, () =>
        {
            Assert.True(plan.RecordContinuation(callback, () => { staleProbes++; return false; }));
            if (throws) throw expected;
            return false;
        });
        if (throws) Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => Attempt()));
        else Assert.False(Attempt());
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation(callback, () => true)));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        Assert.Equal(0, staleProbes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_optional_diagnostics_do_not_become_native_startup_requirements(bool queued)
    {
        var plan = NativePlan(new HookSelection(HookSiteId.DiagnosticBindingsMainLoop, Required: false, Deferred: true));
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => true)));
        if (queued) Assert.True(plan.Install(HookSiteId.DiagnosticBindingsMainLoop, 0x200, 0, () => true));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.DiagnosticBindingsMainLoop)));
    }

    [Fact]
    public void Deferred_evidence_is_not_probed_or_presented_as_complete()
    {
        var plan = NativePlan(new HookSelection(HookSiteId.DxgiPresent, Deferred: true));
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => true)));
        Assert.True(plan.Install(HookSiteId.DxgiPresent, 0x200, 0,
            () => plan.RecordContinuation((Action)Second, () => throw new Exception("deferred probe must not run"))));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        Assert.False(plan.IsGroupComplete(nameof(HookSiteId.DxgiPresent)));
    }

    [Fact]
    public void Missing_template_expansion_count_cannot_be_replaced_by_a_working_probe()
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.AtelRateSettersAllLevelsTemplate)], [],
            out var plan, out _));
        Assert.True(plan!.Install(HookSiteId.AtelRateSettersAllLevelsTemplate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => true)));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void Generation_refusal_invalidates_previously_successful_verification()
    {
        var plan = NativePlan();
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () => true)));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        Assert.False(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x200, 1,
            () => throw new Exception("new generation must not install")));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void Disposed_plans_and_disposal_during_a_probe_cannot_verify()
    {
        var disposed = NativePlan();
        Assert.True(disposed.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => disposed.RecordContinuation((Action)First, () => throw new Exception("disposed probe must not run"))));
        disposed.Dispose();
        Assert.False(disposed.TryVerifyRequiredContinuations(out _));
        var during = NativePlan();
        Assert.True(during.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => during.RecordContinuation((Action)Second, () => { during.Dispose(); return true; })));
        Assert.False(during.TryVerifyRequiredContinuations(out _));
        Assert.Equal(HookRegistrationState.Disposed, during.State);
    }

    [Fact]
    public void Nested_installation_cannot_rebind_the_outer_attempt_or_resurrect_a_refused_plan()
    {
        var plan = NativePlan(new HookSelection(HookSiteId.ModuleSetFlipVSyncInterval));
        int nestedInstallers = 0;
        Assert.False(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
        {
            Assert.False(plan.Install(HookSiteId.ModuleSetFlipVSyncInterval, 0x200, 0,
                () => { nestedInstallers++; return true; }));
            return true;
        }));
        Assert.Equal(0, nestedInstallers);
        Assert.Equal(HookRegistrationState.Refused, plan.State);
    }

    [Fact]
    public void Reentrant_verification_cannot_turn_an_ignored_failure_into_success()
    {
        var plan = NativePlan();
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () =>
            {
                Assert.False(plan.TryVerifyRequiredContinuations(out _));
                return true;
            })));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void Registration_during_verification_is_rejected_before_its_installer()
    {
        var plan = NativePlan(new HookSelection(HookSiteId.ModuleSetFlipVSyncInterval, Required: false));
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0,
            () => plan.RecordContinuation((Action)First, () =>
            {
                Assert.False(plan.Install(HookSiteId.ModuleSetFlipVSyncInterval, 0x200, 0,
                    () => throw new Exception("verification must not mutate its queue")));
                return true;
            })));
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void An_empty_required_set_is_not_positive_continuation_evidence()
    {
        Assert.True(HookRegistrationPlan.TryCreate([], [], out var plan, out _));
        Assert.False(plan!.TryVerifyRequiredContinuations(out string? reason));
        Assert.Contains("required", reason!, StringComparison.OrdinalIgnoreCase);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_receipt_parts_never_reach_native_insertion(bool missingCallback)
    {
        var plan = NativePlan();
        int nativeInsertions = 0;
        Assert.False(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
            plan.RecordContinuation(missingCallback ? null! : (Action)First, missingCallback ? () => true : null!)
            && ++nativeInsertions > 0));
        Assert.Equal(0, nativeInsertions);
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    [Fact]
    public void Verification_inside_an_installer_does_not_probe_or_admit_pending_evidence()
    {
        var plan = NativePlan();
        int probes = 0;
        Assert.True(plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
        {
            Assert.True(plan.RecordContinuation((Action)First, () => { probes++; return true; }));
            Assert.False(plan.TryVerifyRequiredContinuations(out _));
            Assert.Equal(0, probes);
            return true;
        }));
        Assert.True(plan.TryVerifyRequiredContinuations(out _));
        Assert.Equal(1, probes);
    }

    [Fact]
    public void Disposing_then_throwing_from_an_installer_preserves_terminal_disposal_and_the_exception()
    {
        var plan = NativePlan();
        var expected = new InvalidOperationException("queue failed after disposal");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            plan.Install(HookSiteId.ModuleFFXApplicationAnimate, 0x100, 0, () =>
            {
                Assert.True(plan.RecordContinuation((Action)First, () => true));
                plan.Dispose();
                throw expected;
            })));
        Assert.Equal(HookRegistrationState.Disposed, plan.State);
        Assert.False(plan.TryVerifyRequiredContinuations(out _));
    }

    private static HookRegistrationPlan NativePlan(params HookSelection[] extra)
    {
        Assert.True(HookRegistrationPlan.TryCreate([new(HookSiteId.ModuleFFXApplicationAnimate), .. extra], [],
            out var plan, out string? reason), reason);
        return plan!;
    }

    private static HookRegistrationPlan TemplatePlan()
    {
        Assert.True(HookRegistrationPlan.TryCreate(
            [new(HookSiteId.AtelRateSettersAllLevelsTemplate, ExpectedTargets: new nint[] { 0x100, 0x200 })],
            [], out var plan, out string? reason), reason);
        return plan!;
    }

    private static void First() { }
    private static void Second() { }
}
