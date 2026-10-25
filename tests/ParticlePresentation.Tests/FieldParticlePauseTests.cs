using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    internal void EnableFieldPause() => _fieldPauseInstalled = true;
    internal bool DeferFieldFree(nint obj) => _fieldDeferral.TryDeferFree(obj);
    internal void FlushField(nint manager) => field_particle_flush_deferred(manager);
}

[Collection("Particle presentation")]
public unsafe sealed class FieldParticlePauseTests : IDisposable
{
    public FieldParticlePauseTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    private sealed class Fixture : IDisposable
    {
        internal readonly PilotMemory Memory = new();
        internal readonly VrrModule Module = new() { FieldCount = 1 };
        internal nint Manager => (nint)FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        internal nint A => Memory.Address + 0x1000;
        internal nint B => Memory.Address + 0x2000;
        internal Fixture()
        {
            Assert.True(Module.InitializePilot());
            Module.EnableFieldPause();
            new Span<byte>((void*)Manager, 0x80).Clear();
            *(uint*)(Manager + 0x30) = (uint)A;
            *(uint*)A = (uint)B;
            *(uint*)B = 0;
        }
        public void Dispose() => Memory.Dispose();
    }

    [Fact]
    public void PresentationDispatchRunsTheNativeOpsUnderThePause()
    {
        using var f = new Fixture();
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 0);
        int seen = -1, dispatches = 0;
        PilotNative.OnDispatch = _ => { dispatches++; seen = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF); };
        RenderPassContext.IsPresentation = true;
        f.Module.DispatchField(f.Manager);
        Assert.Equal(1, dispatches);
        Assert.Equal(1, seen);
        Assert.Equal(0, FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
    }

    [Fact]
    public void PacedLogicStepDefersRestartAndFreeToTheNextStep()
    {
        using var f = new Fixture();
        var events = new List<string>();
        PilotNative.OnLifecycle = (kind, at) => events.Add($"{kind}:{at - f.Memory.Address:X}");
        PilotNative.OnDispatch = _ => { f.Module.LoopSprite(f.A); Assert.True(f.Module.DeferFieldFree(f.B)); };
        RenderPassContext.IsActive = true;
        f.Module.DispatchField(f.Manager);
        Assert.Empty(events);
        Assert.Equal((uint)f.B, *(uint*)f.A);
        Assert.False(f.Module.DeferFieldFree(f.B));

        f.Module.FlushField(f.Manager);
        Assert.Equal(["loop:1000", "delete:2000"], events);
        Assert.Equal(0u, *(uint*)f.A);
    }

    [Fact]
    public void UnpacedLogicStepRestartsImmediately()
    {
        using var f = new Fixture();
        var events = new List<string>();
        PilotNative.OnLifecycle = (kind, _) => events.Add(kind);
        PilotNative.OnDispatch = _ => { f.Module.LoopSprite(f.A); Assert.False(f.Module.DeferFieldFree(f.B)); };
        f.Module.DispatchField(f.Manager);
        Assert.Equal(["loop"], events);
    }

    [Fact]
    public void DeferralDropsObjectsThatLeftTheManagerAndRespectsCapacity()
    {
        using var memory = new PilotMemory();
        nint manager = memory.Address, a = memory.Address + 0x100, b = memory.Address + 0x200;
        *(uint*)(manager + 0x30) = (uint)a;
        var deferral = new FieldParticleDeferral(capacity: 1) { Scope = manager };
        Assert.True(deferral.TryDeferFree(b));
        Assert.False(deferral.TryDeferFree(a));
        Assert.True(deferral.TryDeferRestart(a));
        deferral.Scope = 0;
        Assert.False(deferral.TryDeferRestart(a));
        var applied = new List<nint>();
        deferral.Flush(manager, applied.Add, applied.Add);
        Assert.Equal([a], applied);
        Assert.Equal(1, deferral.Dropped);

        deferral.Scope = manager;
        Assert.True(deferral.TryDeferRestart(a));
        deferral.Scope = 0;
        deferral.Drop(manager);
        deferral.Flush(manager, applied.Add, applied.Add);
        Assert.Single(applied);
    }

    [Fact]
    public void FreeStubBranchesToAliveOrDieAndCallsItsOwnCell()
    {
        const uint stub = 0x10000000, alive = 0x00717248, die = 0x007173e0;
        byte[] code = FieldParticlePauseCode.FreeStub(stub, alive, die);
        Assert.Equal(FieldParticlePauseCode.FreeStubLength, code.Length);
        Assert.True(FieldParticlePauseCode.Matches(code, "39 46 0C 0F 82 ?? ?? ?? ?? 50 51 52 56 FF 15 ?? ?? ?? ?? 83 C4 04 85 C0 5A 59 58 0F 85 ?? ?? ?? ?? E9"));
        Assert.Equal(alive, FieldParticlePauseCode.RelativeTarget(stub + 3, 6, BitConverter.ToInt32(code, 5)));
        Assert.Equal(stub + 40, BitConverter.ToUInt32(code, 15));
        Assert.Equal(alive, FieldParticlePauseCode.RelativeTarget(stub + 27, 6, BitConverter.ToInt32(code, 29)));
        Assert.Equal(die, FieldParticlePauseCode.RelativeTarget(stub + 33, 5, BitConverter.ToInt32(code, 34)));
    }

    [Fact]
    public void SignatureWildcardsMatchOnlyTheirOwnBytes()
    {
        byte[] code = [0x83, 0x3d, 0x34, 0xfd, 0x30, 0x02, 0x00, 0x75, 0x30];
        Assert.True(FieldParticlePauseCode.Matches(code, "83 3D ?? ?? ?? ?? 00 75 30"));
        Assert.False(FieldParticlePauseCode.Matches(code, "83 3D ?? ?? ?? ?? 00 75 31"));
        Assert.False(FieldParticlePauseCode.Matches(code.AsSpan(0, 4), "83 3D ?? ?? ??"));
    }
}
