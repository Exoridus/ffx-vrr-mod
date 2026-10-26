using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public unsafe sealed class BattleEffectHoldTests : IDisposable
{
    public BattleEffectHoldTests() => PilotNative.Reset();
    public void Dispose() { VrrModule.ReleaseBattleHold(); PilotNative.Reset(); }

    private static VrrModule Installed(PilotMemory memory)
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.EnterParticleLoop();
        module.AttachBattleHold(memory.Address + 0x9000);
        return module;
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, false)]
    [InlineData((byte)0, true)]
    [InlineData((byte)1, true)]
    public void Local_hold_routes_real_worker_and_restores_nested_exception_scope(byte previous, bool throws)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.SetHoldByte(previous);
        var phase = (RenderPassContext.IsPresentation, RenderPassContext.IsActive, RenderPassContext.ImageSerial,
            RenderPassContext.SimulationSerial, RenderPassContext.HistoryEpoch);
        void Run()
        {
            module.WithBattleHold(() =>
            {
                Assert.True(module.OwnsBattleHold);
                Assert.False(RenderPassContext.IsPresentation);
                Assert.True(module.TryBattleHold(out var inner));
                inner();
                Assert.True(module.OwnsBattleHold);
                Assert.Equal(1, module.NativeHold);
                PilotNative.OnWorker = () =>
                {
                    Assert.Equal(3, module.WalkerHold);
                    Assert.False(module.HasPilotCaptureState);
                    if (throws) throw new InvalidOperationException("held worker probe");
                };
                module.RunWorker(0, 1);
            });
        }
        if (throws) Assert.Throws<InvalidOperationException>(Run);
        else Run();
        Assert.Equal(0, module.WalkerHold);
        Assert.Equal(1, PilotNative.Workers);
        Assert.Equal(previous, module.NativeHold);
        Assert.False(module.OwnsBattleHold);
        Assert.False(module.HasPilotCaptureState);
        Assert.Equal(phase, (RenderPassContext.IsPresentation, RenderPassContext.IsActive, RenderPassContext.ImageSerial,
            RenderPassContext.SimulationSerial, RenderPassContext.HistoryEpoch));
        Assert.True(module.PauseNeedsCleanup);
    }

    [Fact]
    public void Foreign_thread_cannot_begin_read_end_or_reset_an_owned_scope()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        Assert.True(module.TryBattleHold(out var leave));
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.False(module.OwnsBattleHold);
                Assert.False(module.TryBattleHold(out _));
                leave();
                module.ResetHoldPass(false);
                Assert.Equal(1, module.NativeHold);
            }
            catch (Exception caught) { error = caught; }
        });
        thread.Start(); thread.Join();
        Assert.Null(error);
        Assert.True(module.OwnsBattleHold);
        leave();
        Assert.Equal(0, module.NativeHold);
        Assert.False(module.OwnsBattleHold);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pass_cleanup_revokes_tokens_and_old_finally_cannot_restore_old_pause(bool setup)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.SetHoldByte(1);
        Assert.True(module.TryBattleHold(out var old));
        if (setup) module.ResetHoldPass(false);
        else module.EndHoldPass();
        Assert.Equal(0, module.NativeHold);
        Assert.False(module.OwnsBattleHold);
        Assert.True(module.TryBattleHold(out var fresh));
        old();
        Assert.Equal(1, module.NativeHold);
        Assert.True(module.OwnsBattleHold);
        fresh(); old();
        Assert.Equal(0, module.NativeHold);
    }

    [Fact]
    public void Non_lifo_or_duplicate_end_cannot_unwind_another_scope()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        Assert.True(module.TryBattleHold(out var outer));
        Assert.True(module.TryBattleHold(out var inner));
        outer();
        Assert.True(module.OwnsBattleHold);
        inner(); outer();
        Assert.False(module.OwnsBattleHold);
        Assert.True(module.TryBattleHold(out var next));
        outer();
        Assert.True(module.OwnsBattleHold);
        next();
        Assert.Equal(0, module.NativeHold);
    }

    [Theory]
    [InlineData("uninstalled")]
    [InlineData("missing_owner")]
    [InlineData("other_instance")]
    [InlineData("null_block")]
    [InlineData("bad_block")]
    [InlineData("outside_loop")]
    public void Failed_begin_leaves_native_byte_and_marker_untouched(string failure)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.SetHoldByte(7);
        module.InvalidateHoldPrerequisite(failure);
        Assert.False(module.TryBattleHold(out _));
        Assert.Equal(7, *(byte*)(memory.Address + 0x9000));
        Assert.False(module.OwnsBattleHold);
    }

    [Theory]
    [InlineData((ushort)0, (byte)0)]
    [InlineData((ushort)0, (byte)2)]
    [InlineData((ushort)1, (byte)1)]
    [InlineData((ushort)2, (byte)1)]
    public void Unexpected_local_worker_tuple_refuses_before_native_work(ushort family, byte mode)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.WithBattleHold(() => module.RunWorker(family, mode));
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal("unexpected_held_worker_scope", module.PresentationFailure);
        Assert.False(module.HasPilotCaptureState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unqualified_or_active_pilot_worker_refuses_local_work(bool activeWorker)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        if (activeWorker) module.SetPilotWorker(true);
        else FhUtil.set_at<byte>(EngineAddresses.BattleState, 1);
        module.WithBattleHold(() => module.RunWorker(0, 1));
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal("unexpected_held_worker_scope", module.PresentationFailure);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(17)]
    public void Real_opp_callback_restores_record_and_associated_buffer(int op)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        nint record = memory.Address + 0x100, scratch = memory.Address + 0x6000, buffer = memory.Address + 0x8000;
        *(uint*)(scratch + 0x220) = (uint)record;
        *(uint*)(record + (op == 9 ? 0xa8 : 0xbc)) = (uint)buffer;
        *(ushort*)(buffer + (op == 9 ? 0 : 4)) = 1;
        byte[] before = new ReadOnlySpan<byte>((void*)record, 0x100).ToArray();
        byte[] bufferBefore = new ReadOnlySpan<byte>((void*)buffer, 0x70).ToArray();
        module.SetOppOriginal(op, &MutateOpp);
        module.WithBattleHold(() => module.CallOpp(op, scratch, buffer));
        Assert.Equal(before, new ReadOnlySpan<byte>((void*)record, 0x100).ToArray());
        if (op is 9 or 17) Assert.Equal(bufferBefore, new ReadOnlySpan<byte>((void*)buffer, 0x70).ToArray());
        else Assert.Equal(99, *(byte*)(buffer + 8));
        Assert.Equal(1, module.OppRestored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_opp_callback_preserves_ended_record_exception(bool changeType)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        nint record = memory.Address + 0x100, scratch = memory.Address + 0x6000;
        *(uint*)(scratch + 0x220) = (uint)record;
        module.SetOppOriginal(1, changeType ? &ChangeOppType : &EndOpp);
        module.WithBattleHold(() => module.CallOpp(1, scratch, 0));
        Assert.Equal(99, *(byte*)record);
        if (changeType) Assert.Equal(4, *(byte*)(record + 0xbb));
        else Assert.Equal(0x8000, *(ushort*)(record + 0xb0));
        Assert.Equal(1, module.OppEnded);
    }

    [Fact]
    public void Real_node_callback_draws_only_and_keeps_exact_selective_restore_ranges()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        nint head = memory.Address + 0x6000, record = memory.Address + 0x7000;
        *(uint*)(head + 8) = (uint)record;
        *(uint*)(record + 0xb0) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, void>)&DrawNode;
        module.SetNodeOriginal(&StateNode);
        _nodeDraws = _nodeStates = 0;
        module.WithBattleHold(() => module.CallNodes(head));
        Assert.Equal(1, _nodeDraws);
        Assert.Equal(0, _nodeStates);
        Assert.Equal(0, *(byte*)(record + 0xc));
        Assert.Equal(0, *(byte*)(record + 0x27f));
        Assert.Equal(0, *(byte*)(record + 0x330));
        Assert.Equal(0, *(byte*)(record + 0x48f));
        Assert.Equal(99, *(byte*)(record + 0x280));
        Assert.Equal(99, *(byte*)(record + 0x32f));
        module.CallNodes(head);
        Assert.Equal(1, _nodeStates);
    }

    [Fact]
    public void Ordinary_simulation_keeps_real_opp_mutations_and_presentation_keeps_restore()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        nint record = memory.Address + 0x100, scratch = memory.Address + 0x6000, buffer = memory.Address + 0x8000;
        *(uint*)(scratch + 0x220) = (uint)record;
        module.SetOppOriginal(1, &MutateOpp);
        module.CallOpp(1, scratch, buffer);
        Assert.Equal(99, *(byte*)record);
        Assert.Equal(0, module.OppRestored);
        *(byte*)record = 7;
        RenderPassContext.IsPresentation = true;
        module.CallOpp(1, scratch, buffer);
        Assert.Equal(7, *(byte*)record);
        Assert.Equal(1, module.OppRestored);
        _nodeDraws = _nodeStates = 0;
        nint head = memory.Address + 0x7000;
        *(uint*)(head + 0xb0) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, void>)&DrawNode;
        module.SetNodeOriginal(&StateNode);
        module.CallNodes(head);
        Assert.Equal(1, _nodeDraws);
        Assert.Equal(0, _nodeStates);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(5, false, false)]
    [InlineData(5, true, false)]
    [InlineData(5, false, true)]
    public void Ppp_pause_is_scoped_to_individual_call_and_preserves_intervening_value(int previous, bool change, bool throws)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, previous);
        module.WithBattleHold(() =>
        {
            Assert.Equal(previous, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
            PilotNative.OnRun = () =>
            {
                Assert.Equal(1, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
                Assert.False(module.HasPilotCaptureState);
                if (change) FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 9);
                if (throws) throw new InvalidOperationException("PPP probe");
            };
            if (throws) Assert.Throws<InvalidOperationException>(() => module.RunSharedManager(1234));
            else Assert.Equal(77u, module.RunSharedManager(1234));
            Assert.Equal(change ? 9 : previous, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
            Assert.False(module.HasPilotCaptureState);
        });
        Assert.Equal(1, PilotNative.Runs);
    }

    [Fact]
    public void Held_ppp_sprite_does_not_consume_fractional_clock_or_enter_capture()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.EnableSpriteClock();
        Assert.True(module.InitializeSpriteClock());
        VrrModule.TargetFramerate = 120;
        nint obj = memory.Address + 0x1000, operand = memory.Address + 0x4000, program = memory.Address + 0x5000;
        *(uint*)(operand + 4) = 3;
        *(short*)(operand + 8) = 15;
        *(uint*)(program + 0xc) = (uint)(memory.Address + 0x6000);
        *(int*)(memory.Address + 0x6000) = 0;
        PilotNative.CreatedObject = obj;
        Assert.Equal(obj, module.CreateSprite(1234, 99));
        Assert.True(module.ScaleSprite(obj, program, 0, 15, out short first));
        Assert.Equal(3, first);
        byte[] before = new ReadOnlySpan<byte>((void*)operand, 12).ToArray();
        PilotNative.OnSprite = (actual, actualOperand, actualProgram) =>
        {
            Assert.Equal(1, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
            Assert.Equal(operand, actualOperand);
            Assert.Equal(before, new ReadOnlySpan<byte>((void*)actualOperand, 12).ToArray());
        };
        PilotNative.OnRun = () => module.DrawSprite(obj, operand, program, false);
        module.WithBattleHold(() => module.RunSharedManager(1234));
        Assert.False(module.HasPilotCaptureState);
        Assert.True(module.ScaleSprite(obj, program, 0, 15, out short next));
        Assert.Equal(4, next);
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
    }

    [Fact]
    public void Walker_restores_preexisting_mode_and_native_simulation_keeps_flags()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        *(byte*)(memory.Address + 0x9001) = 2;
        PilotNative.OnWorker = () => Assert.Equal(3, module.WalkerHold);
        module.WithBattleHold(() => module.RunWorker(0, 1));
        Assert.Equal(2, module.WalkerHold);
        PilotNative.OnWorker = () => Assert.Equal(2, module.WalkerHold);
        module.RunWorker(0, 1);
        Assert.Equal(2, PilotNative.Workers);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 5);
        PilotNative.OnRun = () => Assert.Equal(5, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        Assert.Equal(77u, module.RunSharedManager(1234));
        Assert.Equal(5, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_node_thread_does_not_observe_local_scope_or_step_a_presentation(bool presentation)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        nint head = memory.Address + 0x7000;
        *(uint*)(head + 0xb0) = (uint)(nint)(delegate* unmanaged[Cdecl]<nint, void>)&DrawNode;
        module.SetNodeOriginal(&StateNode);
        _nodeDraws = _nodeStates = 0;
        RenderPassContext.IsPresentation = presentation;
        module.WithBattleHold(() =>
        {
            var thread = new Thread(() => module.CallNodes(head));
            thread.Start(); thread.Join();
        });
        Assert.Equal(0, _nodeDraws);
        Assert.Equal(presentation ? 0 : 1, _nodeStates);
        Assert.Equal(presentation ? "node_thread_changed" : null, module.PresentationFailure);
    }

    private static int _nodeDraws, _nodeStates;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MutateOpp(nint scratch, nint buffer)
    {
        *(byte*)(nint)(*(uint*)(scratch + 0x220)) = 99;
        *(byte*)(buffer + 8) = 99;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EndOpp(nint scratch, nint ctx)
    {
        nint record = (nint)(*(uint*)(scratch + 0x220));
        *(byte*)record = 99; *(ushort*)(record + 0xb0) = 0x8000;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ChangeOppType(nint scratch, nint ctx)
    {
        nint record = (nint)(*(uint*)(scratch + 0x220));
        *(byte*)record = 99; *(byte*)(record + 0xbb) = 4;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DrawNode(nint record)
    {
        _nodeDraws++;
        foreach (int offset in new[] { 0xc, 0x27f, 0x280, 0x32f, 0x330, 0x48f }) *(byte*)(record + offset) = 99;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void StateNode(nint head) => _nodeStates++;
}

public unsafe sealed partial class VrrModule
{
    internal void AttachBattleHold(nint data) { _bx = (byte*)data; s_battle_effects = this; _battleEffectPauseInstalled = true; }
    internal static void ReleaseBattleHold() => s_battle_effects = null;
    internal bool TestSchedulerReady, TestOwnedLoop;
    private bool RenderSchedulerReady => TestSchedulerReady;
    private bool IsOwnedRenderLoop => TestOwnedLoop;
    internal byte NativeHold => _bx[0];
    internal byte WalkerHold => _bx[1];
    internal bool OwnsBattleHold => IsOwnedBattleEffectHold;
    internal bool PauseNeedsCleanup => _bxPresented;
    internal bool HasPilotCaptureState => _particle_pilot_worker || _particle_pilot_dd is not null || _particle_pilot_capture is not null
        || _particle_pilot_mode != 0 || _particle_pilot_owners.Count != 0;
    internal void SetHoldByte(byte value) => _bx[0] = value;
    internal void SetPilotWorker(bool value) => _particle_pilot_worker = value;
    internal bool TryBattleHold(out Action leave)
    {
        bool entered = battle_effects_hold_begin(out var token);
        leave = () => battle_effects_hold_end(token);
        return entered;
    }
    internal void WithBattleHold(Action action)
    {
        Assert.True(TryBattleHold(out var leave));
        try { action(); }
        finally { leave(); }
    }
    internal void ResetHoldPass(bool presentation) => battle_effects_set_pass(presentation);
    internal void EndHoldPass() => battle_effects_end_pass();
    internal void InvalidateHoldPrerequisite(string reason)
    {
        switch (reason)
        {
            case "uninstalled": _battleEffectPauseInstalled = false; break;
            case "missing_owner": _particle_pilot_thread = 0; break;
            case "other_instance": s_battle_effects = new VrrModule(); break;
            case "null_block": _bx = null; break;
            case "bad_block": _bx = (byte*)1234; break;
            case "outside_loop": TestSchedulerReady = true; TestOwnedLoop = false; break;
        }
    }
    internal void SetOppOriginal(int op, delegate* unmanaged[Cdecl]<nint, nint, void> callback) => _bxOppOriginal[op] = (nint)callback;
    internal void CallOpp(int op, nint scratch, nint ctx)
    {
        delegate* unmanaged[Cdecl]<nint, nint, void> callback = op switch
        {
            1 => &bx_on_opp_1,
            9 => &bx_on_opp_9,
            10 => &bx_on_opp_10,
            17 => &bx_on_opp_17,
            _ => throw new ArgumentOutOfRangeException(nameof(op)),
        };
        callback(scratch, ctx);
    }
    internal long OppRestored => _bxOppRestored;
    internal long OppEnded => _bxOppEnded;
    internal void SetNodeOriginal(delegate* unmanaged[Cdecl]<nint, void> callback) => _bxNodeWalkOriginal = (nint)callback;
    internal void CallNodes(nint head) => ((delegate* unmanaged[Cdecl]<nint, void>)&bx_on_node_walk)(head);
}