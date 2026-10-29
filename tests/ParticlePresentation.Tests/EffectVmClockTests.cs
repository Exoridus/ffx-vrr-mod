using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public unsafe sealed class EffectVmClockTests : IDisposable
{
    public EffectVmClockTests() => PilotNative.Reset();
    public void Dispose() { VrrModule.ReleaseBattleHold(); PilotNative.Reset(); }

    private static VrrModule Installed(PilotMemory memory)
    {
        var module = new VrrModule();
        Assert.True(module.InitializePilot());
        module.EnterParticleLoop();
        module.AttachStaticVmForScopeTest(memory.Address + 0x9000);
        module.TestSchedulerReady = module.TestOwnedLoop = true;
        RenderPassContext.IsActive = true;
        return module;
    }

    [Theory]
    [InlineData((byte)0, false)]
    [InlineData((byte)1, false)]
    [InlineData((byte)0, true)]
    [InlineData((byte)1, true)]
    public void Static_worker_holds_only_VM_control_and_restores_after_native_exception(byte mode, bool throws)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        PilotNative.OnWorker = () =>
        {
            Assert.Equal(3, module.WalkerHold);
            Assert.Equal(0, module.NativeHold);
            Assert.False(module.OwnsBattleHold);
            Assert.False(RenderPassContext.IsPresentation);
            if (throws) throw new InvalidOperationException("worker");
        };
        void Run() => module.WithVmClock(() => module.RunWorker(0, mode));
        if (throws) Assert.Throws<InvalidOperationException>(Run);
        else Run();
        Assert.Equal(1, PilotNative.Workers);
        Assert.Equal(0, module.WalkerHold);
        Assert.Equal(0, module.NativeHold);
        Assert.False(module.VmClockOwned);
        Assert.False(module.HasPilotCaptureState);
    }

    [Theory]
    [InlineData((ushort)1, (byte)0)]
    [InlineData((ushort)1, (byte)1)]
    [InlineData((ushort)0, (byte)2)]
    public void Unadmitted_worker_tuple_never_runs(ushort family, byte mode)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.WithVmClock(() => module.RunWorker(family, mode));
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal("unexpected_vm_clock_worker_scope", module.PresentationFailure);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(0, true)]
    public void Shared_PPP_preparation_keeps_its_native_pause_and_exception(int previous, bool throws)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, previous);
        PilotNative.OnRun = () =>
        {
            Assert.True(module.VmClockOwned);
            Assert.Equal(previous, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
            Assert.False(module.OwnsBattleHold);
            Assert.False(RenderPassContext.IsPresentation);
            if (throws) throw new InvalidOperationException("PPP");
        };
        void Run() => module.WithVmClock(() => Assert.Equal(77u, module.RunSharedManager(1234)));
        if (throws) Assert.Throws<InvalidOperationException>(Run);
        else Run();
        Assert.Equal(1, PilotNative.Runs);
        Assert.Equal(previous, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        Assert.False(module.VmClockOwned);
        Assert.False(module.HasPilotCaptureState);
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public void Extra_image_static_worker_prepares_without_advancing_PC_or_WAIT(byte mode)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        RenderPassContext.IsPresentation = true;
        int pc = 20, wait = 4, preparation = 0;
        PilotNative.OnWorker = () =>
        {
            preparation++;
            if (module.WalkerHold != 3) { pc += 4; wait--; }
            Assert.True(RenderPassContext.IsPresentation);
            Assert.Equal(0, module.NativeHold);
        };
        module.RunWorker(0, mode);
        Assert.Equal(1, preparation);
        Assert.Equal(20, pc);
        Assert.Equal(4, wait);
        Assert.Equal(0, module.WalkerHold);
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        Assert.False(module.HasPilotCaptureState);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("phase")]
    [InlineData("nested")]
    [InlineData("qualified")]
    public void Extra_image_static_worker_rejects_lost_scope_before_native(string failure)
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        RenderPassContext.IsPresentation = true;
        if (failure == "owner") VrrModule.ReleaseBattleHold();
        if (failure == "phase") module.TestOwnedLoop = false;
        if (failure == "nested") module.SetPilotWorker(true);
        if (failure == "qualified") FhUtil.set_at<byte>(EngineAddresses.BattleState, 1);
        module.RunWorker(0, 0);
        Assert.Equal(0, PilotNative.Workers);
        Assert.NotNull(module.PresentationFailure);
    }
    [Fact]
    public void Lost_owned_phase_cannot_fall_through_to_normal_VM_execution()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        module.WithVmClock(() =>
        {
            module.TestOwnedLoop = false;
            module.RunWorker(0, 1);
        });
        Assert.Equal(0, PilotNative.Workers);
        Assert.Equal("unexpected_vm_clock_worker_scope", module.PresentationFailure);
    }

    [Fact]
    public void Scope_tokens_require_owner_thread_LIFO_and_current_pass()
    {
        using var memory = new PilotMemory();
        var module = Installed(memory);
        Assert.True(module.TryVmClock(out var outer));
        Assert.True(module.TryVmClock(out var inner));
        outer();
        Assert.True(module.VmClockOwned);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Assert.False(module.VmClockOwned); Assert.False(module.TryVmClock(out _)); inner(); module.ResetHoldPass(false); }
            catch (Exception caught) { failure = caught; }
        });
        thread.Start(); thread.Join();
        Assert.Null(failure);
        Assert.True(module.VmClockOwned);
        inner(); outer();
        Assert.False(module.VmClockOwned);
        Assert.True(module.TryVmClock(out var old));
        module.ResetHoldPass(false);
        Assert.True(module.TryVmClock(out var fresh));
        old();
        Assert.True(module.VmClockOwned);
        fresh();
        Assert.False(module.VmClockOwned);
    }
}

public unsafe sealed partial class VrrModule
{
    private bool OwnsRenderPass => TestSchedulerReady && TestOwnedLoop && RenderPassContext.IsActive;
    internal void AttachStaticVmForScopeTest(nint data)
    {
        _bx = (byte*)data; s_battle_effects = this; _staticEffectVmClockInstalled = true; _battleEffectPauseInstalled = false;
    }
    internal bool VmClockOwned => IsOwnedEffectVmClockHold;
    internal bool TryVmClock(out Action leave)
    {
        bool entered = effect_vm_clock_begin(out var token);
        leave = () => effect_vm_clock_end(token);
        return entered;
    }
    internal void WithVmClock(Action action)
    {
        Assert.True(TryVmClock(out var leave));
        try { action(); }
        finally { leave(); }
    }
}