using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadSnapshotTests : IDisposable
{
    public PadSnapshotTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        NativeSnapshotFixture.Reset();
        NativeSnapshotBridge.Available = true;
        NativeSnapshotBridge.CreateCalls = 0;
        NativeClock.SnapshotCalls = NativeClock.ExecCalls = 0;
        NativeClock.SnapshotAction = NativeClock.ExecAction = NativeClock.Advance = null;
        NativeClock.DiagnosticExecRead = null;
        NativeClock.ExecValue = 0xfedcba98u;
        NativeClock.LoopAction = null;
        NativeClock.HistoryHookAction = null;
        NativeClock.HistoryHookRegistrations.Clear();
        FhUtil.Writes.Clear();
        FhUtil.MenuReservation = -1;
        FhUtil.MainMenu = FhUtil.MenuPause = FhUtil.Overdrive = 0;
        FhUtil.Dummy = 0;
        RenderPassContext.IsPresentation = RenderPassContext.IsActive = false;
    }

    private static VrrModule Module(bool capture = false)
    {
        var module = new VrrModule { SyntheticSnapshotReady = false };
        module.BeginRealSnapshotLifecycle();
        module.EnableAdapter();
        Assert.True(module.InitHistory());
        module.CaptureLoops(capture);
        module.PreparePass();
        return module;
    }

    private static void Pass(VrrModule module, bool presentation, Action? body = null)
    {
        if (presentation) module.PreparePresentation(); else module.PreparePass();
        NativeClock.LoopAction = _ => { module.RunNativePass(); (body ?? module.CallSnapshot)(); module.ReadExec(); };
        module.CallDiagnosticLoop(1f / 60);
    }

    [Fact]
    public void Exit_diagnostic_sampling_failure_cannot_skip_input_or_scheduler_cleanup()
    {
        var module = Module(true);
        Pass(module, false);
        module.EnableSnapshotExitDiagnostic();
        int reads = 0;
        NativeClock.DiagnosticExecRead = () => ++reads == 2
            ? throw new InvalidOperationException("exit diagnostic fixture") : module.ReadExec();

        Pass(module, true);

        Assert.Equal(2, reads);
        Assert.True(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.False(module.SnapshotPresentationOwned);
    }
    [Fact]
    public void Exit_diagnostic_samples_the_held_exec_before_owner_teardown_and_preserves_later_images()
    {
        var module = Module(true);
        module.EnableSnapshotExitDiagnostic();
        var ownedReads = new List<bool>();
        NativeClock.DiagnosticExecRead = () =>
        {
            if (module.SnapshotHeld) ownedReads.Add(module.SnapshotPresentationOwned);
            return module.ReadExec();
        };
        Pass(module, false);
        Assert.True(module.SnapshotBaseline);
        int snapshots = NativeClock.SnapshotCalls;

        Pass(module, true);
        Assert.True(module.SnapshotBaseline);
        Pass(module, true);

        Assert.True(module.SnapshotBaseline);
        Assert.Equal(snapshots, NativeClock.SnapshotCalls);
        Assert.Equal(new[] { true, true }, ownedReads);
        var exits = module.DiagnosticEvents.Where(e => e.Kind == "loop_exit").TakeLast(2);
        foreach (var exit in exits)
            Assert.Equal(0u, JsonSerializer.SerializeToElement(exit.Data).GetProperty("effective_exec").GetUInt32());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_dispatch_seeds_only_after_complete_simulation_and_holds_repeated_images(bool capture)
    {
        var module = Module(capture);
        Pass(module, false);
        Assert.True(module.SnapshotBaseline);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Pass(module, true, () =>
        {
            int writes = FhUtil.Writes.Count;
            module.CallSnapshot();
            Assert.Equal(writes, FhUtil.Writes.Count);
        });
        Pass(module, true);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.True(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.DoesNotContain(FhUtil.Writes, w => NativeSnapshotFixture.GuardTypes.ContainsKey(w.Address));
    }

    [Fact]
    public void Registered_callback_routes_to_real_producer_and_uses_the_same_chain_delegate()
    {
        var module = Module();
        var hook = Assert.Single(NativeClock.HistoryHookRegistrations, h => h.Rva == 0x42d070);
        Assert.Same(NativeSnapshotBridge.Entry, hook.Callback);
        int calculate = NativeClock.CalculateCalls;
        Pass(module, false, () => hook.Callback.DynamicInvoke());
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.Equal(calculate, NativeClock.CalculateCalls);
        Pass(module, true, () => hook.Callback.DynamicInvoke());
        Assert.Equal(1, NativeClock.SnapshotCalls);
    }

    [Theory]
    [InlineData("bridge")]
    [InlineData("call_opcode")]
    [InlineData("call_target")]
    [InlineData("binding")]
    public void Snapshot_preflight_failure_registers_none_of_the_candidate_input_group(string failure)
    {
        var module = new VrrModule().EnableAdapter();
        if (failure == "bridge") NativeSnapshotBridge.Available = false;
        if (failure == "call_opcode") NativeSnapshotFixture.CallEntry[0] = 0xe9;
        if (failure == "call_target") NativeSnapshotFixture.CallEntry[1] ^= 1;
        if (failure == "binding") NativeSnapshotFixture.Unreadable = 0xd33350;
        Assert.False(module.InitHistory());
        Assert.False(module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }

    [Fact]
    public void Global_qualification_veto_never_loads_or_registers_the_snapshot_bridge()
    {
        var module = new VrrModule();
        Assert.True(module.Preflight());
        Assert.False(module.Effective);
        Assert.True(module.InitHistory());
        Assert.Equal(0, NativeSnapshotBridge.CreateCalls);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }

    [Theory]
    [InlineData("no_clock")]
    [InlineData("no_producer")]
    [InlineData("wrong_return")]
    [InlineData("epoch")]
    [InlineData("native_owner")]
    [InlineData("partial_clock")]
    public void Incomplete_or_unidentified_simulation_cannot_seed_baseline(string failure)
    {
        var module = Module();
        NativeClock.LoopAction = _ =>
        {
            if (failure == "partial_clock") module.RunClock();
            else if (failure != "no_clock") module.RunNativePass();
            if (failure == "epoch") RenderPassContext.ResetHistory();
            if (failure == "native_owner") module.NativeMovie = true;
            if (failure != "no_producer")
                module.CallSnapshotAt(failure == "wrong_return" ? NativeSnapshotFixture.Continuation + 1 : NativeSnapshotFixture.Continuation);
        };
        module.CallDiagnosticLoop(1f / 60);
        Assert.False(module.SnapshotBaseline);
        Assert.Equal(failure == "no_producer" ? 0 : 1, NativeClock.SnapshotCalls);
    }

    [Theory]
    [InlineData("cleanup")]
    [InlineData("native")]
    [InlineData("producer_epoch")]
    [InlineData("producer_owner")]
    public void Late_failure_never_commits_a_reached_normal_producer(string failure)
    {
        var module = Module();
        var error = new InvalidOperationException("snapshot fixture");
        if (failure == "cleanup") module.DiagnosticCleanup = () => throw error;
        if (failure == "producer_epoch") NativeClock.SnapshotAction = RenderPassContext.ResetHistory;
        if (failure == "producer_owner") NativeClock.SnapshotAction = () => module.NativeSync = true;
        NativeClock.LoopAction = _ =>
        {
            module.RunNativePass();
            module.CallSnapshot();
            if (failure == "native") throw error;
        };
        if (failure is "cleanup" or "native") Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        else module.CallDiagnosticLoop(1f / 60);
        Assert.False(module.SnapshotBaseline);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotHeld);
    }

    public static IEnumerable<object[]> Guards()
    {
        yield return [0xd33350, 0x100u];
        yield return [0x1fc44a8, 0x10u];
        foreach (int rva in new[] { 0xf3c910, 0xf007b0, 0xf007b1, 0xf007b3 }) yield return [rva, 0xffu];
        yield return [0x1fc44b4, 0x100u];
    }

    [Theory]
    [MemberData(nameof(Guards))]
    public void Exact_typed_native_guard_blocks_hold_and_cannot_be_cleared_to_rearm(int rva, uint value)
    {
        var module = Module();
        Pass(module, false);
        NativeSnapshotFixture.Write(rva, value);
        Pass(module, true);
        NativeSnapshotFixture.Write(rva, 0);
        Pass(module, true);
        Assert.Equal(3, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(0x80000000u)]
    public void Private_press_bits_other_than_menu_bit_do_not_block_the_bounded_hold(uint value)
    {
        var module = Module();
        Pass(module, false);
        NativeSnapshotFixture.Write(0x1fc44a8, value);
        Pass(module, true);
        Assert.Equal(1, NativeClock.SnapshotCalls);
    }

    [Theory]
    [InlineData("reservation")]
    [InlineData("main_menu")]
    [InlineData("pause")]
    [InlineData("overdrive")]
    [InlineData("dummy255")]
    [InlineData("movie")]
    [InlineData("sync")]
    [InlineData("readiness")]
    [InlineData("admission")]
    [InlineData("unreadable")]
    public void Fresh_owner_readiness_or_binding_change_retains_native_and_invalidates(string owner)
    {
        var module = Module();
        Pass(module, false);
        Pass(module, true, () =>
        {
            switch (owner)
            {
                case "reservation": FhUtil.MenuReservation = 255; break;
                case "main_menu": FhUtil.MainMenu = 1; break;
                case "pause": FhUtil.MenuPause = 1; break;
                case "overdrive": FhUtil.Overdrive = 1; break;
                case "dummy255": FhUtil.Dummy = 255; break;
                case "movie": module.NativeMovie = true; break;
                case "sync": module.NativeSync = true; break;
                case "readiness": module.RevokeSnapshotReadiness(); break;
                case "admission": module.RefusePresentation("fixture"); break;
                case "unreadable": NativeSnapshotFixture.Unreadable = 0xf007b0; break;
            }
            module.CallSnapshot();
        });
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nested_loop_invalidates_without_releasing_outer_hold(bool throws)
    {
        var module = Module();
        Pass(module, false);
        int depth = 0;
        module.PreparePresentation();
        NativeClock.LoopAction = delta =>
        {
            if (depth++ == 0)
            {
                module.RunNativePass();
                module.CallSnapshot();
                Assert.True(module.SnapshotHeld);
                try { module.CallDiagnosticLoop(delta); }
                catch (InvalidOperationException) when (throws) { }
                Assert.True(module.SnapshotHeld);
                Assert.Equal(0u, module.ReadExec());
                module.CallSnapshot();
            }
            else
            {
                module.RunNativePass();
                module.CallSnapshot();
                if (throws) throw new InvalidOperationException("nested fixture");
            }
        };
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(3, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.Equal(0xfedcba98u, module.ReadExec());
    }

    [Fact]
    public void Recursive_producer_invalidates_the_outer_normal_return()
    {
        var module = Module();
        int calls = 0;
        NativeClock.SnapshotAction = () => { if (calls++ == 0) module.CallSnapshot(); };
        Pass(module, false);
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData("phase")]
    [InlineData("readiness")]
    [InlineData("epoch")]
    [InlineData("frame")]
    public void Exec_remains_native_first_zero_until_matching_root_exit_after_a_hold(string change)
    {
        var module = Module();
        Pass(module, false);
        Pass(module, true, () =>
        {
            module.CallSnapshot();
            Assert.True(module.SnapshotHeld);
            if (change == "phase") RenderPassContext.IsPresentation = false;
            if (change == "readiness") module.RevokeSnapshotReadiness();
            if (change == "epoch") RenderPassContext.ResetHistory();
            if (change == "frame") module.RevokePadFrame(true);
            int before = NativeClock.ExecCalls;
            NativeClock.ExecAction = () => Assert.True(module.SnapshotHeld);
            Assert.Equal(0u, module.ReadExec());
            Assert.Equal(before + 1, NativeClock.ExecCalls);
            NativeClock.ExecAction = null;
            uint foreign = 0;
            var thread = new Thread(() => foreign = module.ReadExec());
            thread.Start();
            thread.Join();
            Assert.Equal(0xfedcba98u, foreign);
        });
        Assert.Equal(0xfedcba98u, module.ReadExec());
        Assert.False(module.SnapshotBaseline);
    }

    [Fact]
    public void Clock_reentrant_producer_cannot_claim_a_completed_clock()
    {
        var module = Module();
        NativeClock.Advance = module.CallSnapshot;
        Pass(module, false);
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Fact]
    public void Native_producer_exception_is_never_retried_by_the_managed_invocation()
    {
        var module = Module();
        var error = new InvalidOperationException("producer fixture");
        NativeClock.SnapshotAction = () => throw error;
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.CallSnapshotCore(); };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Fact]
    public void Policy_failure_before_native_forwards_once_and_refuses_future_admission()
    {
        var module = Module();
        NativeSnapshotFixture.ThrowOnRead = true;
        Pass(module, false);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
        Assert.Equal("pad_snapshot_policy_failure", module.InputGateReason);
    }

    [Fact]
    public void Baseline_is_not_committed_until_late_diagnostic_cleanup_returns()
    {
        var module = Module(true);
        var error = new InvalidOperationException("late diagnostic fixture");
        module.DiagnosticCleanup = () => Assert.False(module.SnapshotBaseline);
        module.DiagnosticEventAction = kind =>
        {
            if (kind != "loop_exit") return;
            Assert.False(module.SnapshotBaseline);
            throw error;
        };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => Pass(module, false)));
        Assert.False(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }

    [Fact]
    public void Cleanup_failure_preserves_the_original_native_error_and_clears_root_scope()
    {
        var module = Module();
        var native = new InvalidOperationException("native fixture");
        module.DiagnosticCleanup = () => throw new ArgumentException("cleanup fixture");
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.CallSnapshot(); throw native; };
        Assert.Same(native, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        Assert.False(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }

    [Fact]
    public void Foreign_producer_and_loop_invalidate_baseline_but_keep_the_owning_hold_commitment()
    {
        var module = Module();
        Pass(module, false);
        Pass(module, true, () =>
        {
            module.CallSnapshot();
            NativeClock.LoopAction = _ => module.CallSnapshot();
            uint foreign = 0;
            var thread = new Thread(() =>
            {
                module.CallDiagnosticLoop(1f / 60);
                foreign = module.ReadExec();
            });
            thread.Start();
            thread.Join();
            Assert.Equal(0xfedcba98u, foreign);
            Assert.True(module.SnapshotHeld);
            Assert.Equal(0u, module.ReadExec());
        });
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_producer_during_native_or_cleanup_cannot_be_overwritten_by_root_commit(bool cleanup)
    {
        var module = Module();
        int calls = 0;
        Action foreign = () =>
        {
            var thread = new Thread(module.CallSnapshot);
            thread.Start();
            thread.Join();
        };
        if (cleanup) module.DiagnosticCleanup = foreign;
        else NativeClock.SnapshotAction = () => { if (Interlocked.Increment(ref calls) == 1) foreign(); };
        Pass(module, false);
        Assert.Equal(2, NativeClock.SnapshotCalls);
        Assert.False(module.SnapshotBaseline);
        Assert.False(module.SnapshotHeld);
        Assert.Equal("pad_snapshot_unowned_producer", module.InputGateReason);
    }

    [Fact]
    public void Readiness_revoked_during_cleanup_prevents_baseline_commit()
    {
        var module = Module();
        module.DiagnosticCleanup = () => module.RevokePadFrame(true);
        Pass(module, false);
        Assert.False(module.SnapshotBaseline);
        Assert.Equal(1, NativeClock.SnapshotCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Real_first_plan_reset_then_zero_steps_recover_through_native_without_presenting_unbased_input(bool bypass, bool capture)
    {
        var module = Module(capture);
        module.RealSnapshotImage(10);
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
        NativeClock.LoopAction = _ =>
        {
            module.RunNativePass();
            if (!bypass) module.CallSnapshot();
            module.ReadExec();
        };
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(!bypass, module.SnapshotBaseline);
        module.RealSnapshotImage(10.001);
        NativeClock.LoopAction = _ =>
        {
            module.RunNativePass();
            Assert.Equal(!bypass, RenderPassContext.IsPresentation);
            module.CallSnapshot();
            Assert.Equal(bypass ? 0xfedcba98u : 0u, module.ReadExec());
        };
        module.CallDiagnosticLoop(1f / 60);
        Assert.True(module.SnapshotBaseline);
        Assert.Equal(0, module.SnapshotPlan.DroppedSteps);
        int before = NativeClock.SnapshotCalls;
        module.RealSnapshotImage(10.002);
        NativeClock.LoopAction = _ =>
        {
            module.RunNativePass();
            Assert.True(RenderPassContext.IsPresentation);
            module.CallSnapshot();
            Assert.Equal(0u, module.ReadExec());
        };
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(before, NativeClock.SnapshotCalls);
    }

    [Theory]
    [InlineData("return")]
    [InlineData("guard")]
    [InlineData("baseline")]
    [InlineData("duplicate")]
    public void Rejected_owned_presentation_refuses_the_current_image_before_forwarding_once(string failure)
    {
        var module = Module();
        Pass(module, false);
        var image = module.BeginSnapshotAdmissionImage();
        int before = NativeClock.SnapshotCalls;
        bool rejectedBeforeNative = false;
        Pass(module, true, () =>
        {
            Assert.True(RenderPassContext.IsPresentation);
            if (failure == "guard") NativeSnapshotFixture.Write(0xf007b0, 1);
            if (failure == "baseline") module.ClearSnapshotBaseline();
            if (failure == "duplicate") module.CallSnapshot();
            NativeClock.SnapshotAction = () => rejectedBeforeNative = image.IsRejected;
            module.CallSnapshotAt(NativeSnapshotFixture.Continuation + (failure == "return" ? 1u : 0u));
            Assert.True(rejectedBeforeNative);
            Assert.True(image.IsRejected);
            Assert.True(RenderPassContext.IsPresentation);
        });
        Assert.Equal(before + 1, NativeClock.SnapshotCalls);
        Assert.NotEqual("", module.InputGateReason);
    }

    [Fact]
    public void Normal_clock_and_producer_without_positive_effective_exec_cannot_seed_baseline()
    {
        var module = Module();
        NativeClock.ExecValue = 0;
        Pass(module, false);
        Assert.False(module.SnapshotBaseline);
    }

    [Fact]
    public void Exec_observed_before_the_identified_producer_is_not_completed_simulation_proof()
    {
        var module = Module();
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.ReadExec(); module.CallSnapshot(); };
        module.CallDiagnosticLoop(1f / 60);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData(2f, 2f, 1)]
    [InlineData(8f, 8f, 4)]
    [InlineData(2f, 8f, 4)]
    [InlineData(8f, 2f, 1)]
    [InlineData(16f, 16f, 4)]
    public void Native_warmup_anchors_the_actual_normalized_speed_without_replaying_its_tick(float warmupSpeed, float nextSpeed, int due)
    {
        var module = Module();
        module.RealSnapshotImage(10);
        NativeClock.LoopAction = _ => { module.SnapshotClock(warmupSpeed / 60); module.CallSnapshot(); module.ReadExec(); };
        module.CallDiagnosticLoop(1f / 60);
        Assert.True(module.SnapshotBaseline);
        Assert.Equal(1, module.CompletionState.SimulationTicks);
        int nativeClocks = 0;
        NativeClock.Advance = () => nativeClocks++;
        module.RealSnapshotImage(10.001);
        NativeClock.LoopAction = _ => { module.SnapshotClock(nextSpeed / 60); module.CallSnapshot(); module.ReadExec(); };
        module.CallDiagnosticLoop(1f / 60);
        if (warmupSpeed != nextSpeed)
        {
            Assert.Equal(1, module.SnapshotPlan.Steps);
            Assert.Equal(1, nativeClocks);
            Assert.Equal(2, module.CompletionState.SimulationTicks);
            Assert.Null(module.InputGateReason.Length == 0 ? null : module.InputGateReason);
            module.RealSnapshotImage(10.01);
            module.CallDiagnosticLoop(1f / 60);
            Assert.True(module.SnapshotBaseline);
            module.RealSnapshotImage(10.02);
            module.CallDiagnosticLoop(1f / 60);
            Assert.Equal(due, module.SnapshotPlan.Steps);
            Assert.Equal(3, nativeClocks);
            return;
        }
        Assert.Equal(0, module.SnapshotPlan.Steps);
        Assert.Equal(0, nativeClocks);
        Assert.Equal(1, module.CompletionState.SimulationTicks);
        module.RealSnapshotImage(10.01);
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(due, module.SnapshotPlan.Steps);
        Assert.Equal(0, module.SnapshotPlan.DroppedSteps);
        Assert.Equal(1, nativeClocks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Zero_native_exec_keeps_real_startup_at_native_pacing_until_a_complete_tick(bool capture)
    {
        var module = Module(capture);
        NativeClock.ExecValue = 0;
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.CallSnapshot(); module.ReadExec(); };
        foreach (double now in new[] { 10d, 10.001 })
        {
            module.RealSnapshotImage(now);
            Assert.False(RenderPassContext.IsActive);
            Assert.Equal(60, module.ReportedRate);
            module.CallDiagnosticLoop(1f / 60);
            Assert.False(module.SnapshotBaseline);
        }
        NativeClock.ExecValue = 1;
        module.RealSnapshotImage(10.002);
        module.CallDiagnosticLoop(1f / 60);
        Assert.True(module.SnapshotBaseline);
        module.RealSnapshotImage(10.003);
        Assert.True(RenderPassContext.IsActive);
        module.CallDiagnosticLoop(1f / 60);
        Assert.Equal(0, module.SnapshotPlan.Steps);
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("epoch")]
    [InlineData("guard")]
    [InlineData("field_ready")]
    [InlineData("image_ready")]
    [InlineData("pad_ready")]
    public void Already_admitted_image_losing_snapshot_readiness_is_rejected_before_the_clock_advances(string change)
    {
        var module = Module();
        module.RealSnapshotImage(10);
        NativeClock.LoopAction = _ => { module.RunNativePass(); module.CallSnapshot(); module.ReadExec(); };
        module.CallDiagnosticLoop(1f / 60);
        var image = module.BeginSnapshotAdmissionImage();
        module.RealSnapshotImage(10.001);
        Assert.True(RenderPassContext.IsActive);
        SimulationFramePlan before = module.SnapshotPlan;
        if (change == "baseline") module.ClearSnapshotBaseline();
        if (change == "epoch") RenderPassContext.ResetHistory();
        if (change == "guard") NativeSnapshotFixture.Write(0xf007b0, 1);
        if (change == "field_ready") module.ProducerQualified = false;
        if (change == "image_ready") module.ImageReady = false;
        if (change == "pad_ready") module.RevokeSnapshotReadiness();
        bool rejectedBeforeNativeClock = false;
        NativeClock.Advance = () => rejectedBeforeNativeClock = image.IsRejected;
        module.CallDiagnosticLoop(1f / 60);
        Assert.True(rejectedBeforeNativeClock);
        Assert.True(image.IsRejected);
        Assert.Equal(before, module.SnapshotPlan);
        Assert.False(module.SnapshotBaseline);
    }

    [Theory]
    [InlineData("epoch", false)]
    [InlineData("epoch", true)]
    [InlineData("owner", false)]
    [InlineData("owner", true)]
    [InlineData("cleanup", false)]
    [InlineData("cleanup", true)]
    [InlineData("image_ready", false)]
    [InlineData("image_ready", true)]
    [InlineData("field_ready", false)]
    [InlineData("field_ready", true)]
    public void Real_native_warmup_requires_unchanged_owners_epoch_all_eligibility_and_cleanup(string failure, bool capture)
    {
        var module = Module(capture);
        module.RealSnapshotImage(10);
        if (failure == "epoch") NativeClock.SnapshotAction = RenderPassContext.ResetHistory;
        if (failure == "owner") NativeClock.SnapshotAction = () => module.NativeMovie = true;
        var error = new InvalidOperationException("warmup cleanup fixture");
        if (failure == "cleanup") module.DiagnosticCleanup = () => throw error;
        NativeClock.LoopAction = _ =>
        {
            module.RunNativePass();
            module.CallSnapshot();
            module.ReadExec();
            if (failure == "image_ready") module.ImageReady = false;
            if (failure == "field_ready") module.ProducerQualified = false;
        };
        if (failure == "cleanup") Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
        else module.CallDiagnosticLoop(1f / 60);
        Assert.False(module.SnapshotBaseline);
        module.RealSnapshotImage(10.001);
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool SnapshotBaseline => _padSnapshotBaseline;
    internal bool SnapshotHeld => _padSnapshotHeld;
    internal bool SnapshotPresentationOwned => IsOwnedPresentationPass;
    internal void EnableSnapshotExitDiagnostic() => _diagnosticExecAvailable = true;
    internal void CallSnapshot() => CallSnapshotAt(NativeSnapshotFixture.ReturnAddress);
    internal void CallSnapshotAt(nuint address) => _padSnapshotDispatch!(address, 0);
    internal void CallSnapshotCore() => pad_snapshot_invoke(NativeSnapshotFixture.ReturnAddress, NativeClock.Snapshot);
    internal void RevokeSnapshotReadiness() => _pad_history_readers_ready = false;
    internal void ClearSnapshotBaseline() => _padSnapshotBaseline = false;
    internal void BeginRealSnapshotLifecycle()
    {
        SyntheticSnapshotReady = _padSnapshotBaseline = _render_snapshot_ready_at_frame = _render_snapshot_warmup = false;
    }
    internal PresentationAdmission.Image BeginSnapshotAdmissionImage() => _presentationAdmission.BeginImage();
    internal SimulationFramePlan SnapshotPlan => _render_plan;
    internal void RealSnapshotImage(double now) { BeginFrame(); _render_now = now; }
    internal void SnapshotClock(float nativeDelta) => h_advance_vblank(nativeDelta);
}

internal static partial class NativeClock
{
    internal static int SnapshotCalls;
    internal static Action? SnapshotAction;
    public static void Snapshot() { SnapshotCalls++; SnapshotAction?.Invoke(); }
}

internal static class NativeSnapshotBridge
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Dispatch(nuint returnAddress, nuint returnSlot);
    internal static bool Available = true;
    internal static int CreateCalls;
    internal static Delegate? Entry;
    private static readonly List<Delegate> Roots = [];
    internal static Delegate? PadEntry;
    internal static bool TryCreatePadLatch<T>(Dispatch dispatch, out T? entry) where T : Delegate
    {
        entry = null;
        if (!Available) return false;
        Action thunk = () => dispatch(0, 0);
        entry = (T)Delegate.CreateDelegate(typeof(T), thunk.Target, thunk.Method);
        PadEntry = entry;
        Roots.Add(entry);
        return true;
    }
    internal static bool TryCreate<T>(Dispatch dispatch, out T? entry) where T : Delegate
    {
        CreateCalls++;
        entry = null;
        if (!Available) return false;
        Action thunk = () => dispatch(NativeSnapshotFixture.ReturnAddress, 0);
        entry = (T)Delegate.CreateDelegate(typeof(T), thunk.Target, thunk.Method);
        Entry = entry;
        Roots.Add(entry);
        return true;
    }
}

internal static unsafe class NativeSnapshotFixture
{
    internal const nuint Continuation = 0x820df5;
    internal static nuint ReturnAddress = Continuation;
    internal static byte[] CallEntry = Convert.FromHexString("E87BC20000");
    internal static nint Unreadable;
    internal static bool ThrowOnRead;
    internal static readonly Dictionary<nint, Type> GuardTypes = new()
    {
        [0xd33350] = typeof(uint), [0x1fc44a8] = typeof(uint), [0x1fc44b4] = typeof(uint),
        [0xf3c910] = typeof(byte), [0xf007b0] = typeof(byte), [0xf007b1] = typeof(byte), [0xf007b3] = typeof(byte),
    };
    private static readonly Dictionary<nint, byte[]> Memory = GuardTypes.ToDictionary(p => p.Key, _ => GC.AllocateArray<byte>(4, pinned: true));
    internal static void Reset()
    {
        foreach (var data in Memory.Values) data.AsSpan().Clear();
        CallEntry = Convert.FromHexString("E87BC20000");
        Unreadable = 0;
        ThrowOnRead = false;
        ReturnAddress = Continuation;
    }
    internal static void Write(nint rva, uint value)
    {
        if (GuardTypes[rva] == typeof(byte)) Memory[rva][0] = checked((byte)value);
        else BitConverter.TryWriteBytes(Memory[rva], value);
    }
    internal static bool TryPointer<T>(nint rva, out nint pointer) where T : unmanaged
    {
        pointer = 0;
        if (rva == 0x420df5) { pointer = (nint)Continuation; return true; }
        if (rva == 0x422224) { pointer = 0x822224; return true; }
        if (!GuardTypes.TryGetValue(rva, out Type? type)) return false;
        if (ThrowOnRead) throw new InvalidOperationException("Snapshot binding fixture failed.");
        if (type != typeof(T)) throw new InvalidOperationException($"Wrong snapshot guard type at 0x{rva:x}: {typeof(T).Name}");
        if (rva == Unreadable) return true;
        fixed (byte* data = Memory[rva]) pointer = (nint)data;
        return true;
    }
}
