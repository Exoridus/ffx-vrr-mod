using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    internal void DispatchField(nint manager) => h_field_particle_dispatch(manager);
    internal void OuterField() => h_ppp_fp_loop();
    internal void WithoutRestartHold() => _config.FieldParticleRestartHold = false;
    internal bool FieldQualified() => field_particle_presentation_is_qualified();
}

[Collection("Particle presentation")]
public unsafe sealed class FieldParticleAdapterTests : IDisposable
{
    public FieldParticleAdapterTests() => PilotNative.Reset();
    public void Dispose() => PilotNative.Reset();

    private sealed class Fixture : IDisposable
    {
        internal readonly PilotMemory Memory = new();
        internal readonly VrrModule Module = new() { FieldCount = 1 };
        internal nint Manager => (nint)FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        internal nint Object => Memory.Address+0x1000;
        internal nint Kernel => (nint)FhUtil.ptr_at<byte>(0x83a508)+49*40;
        internal Fixture()
        {
            Assert.True(Module.InitializePilot());
            nint entry=Memory.Address+0x2000, group=Memory.Address+0x3000;
            new Span<byte>((void*)Manager,0x80).Clear();
            *(int*)Manager=-1; *(uint*)(Manager+0x30)=(uint)Object;
            *(int*)(Manager+0x18)=1; *(uint*)(Manager+0x3c)=(uint)entry;
            *(uint*)entry=(uint)group; *(uint*)(entry+4)=(uint)Object; *(ushort*)(entry+12)=1;
            *(uint*)Object=0; *(uint*)(Object+8)=(uint)entry;
            *(int*)(group+0x20)=0x100; *(short*)(group+0x26)=1; *(byte*)(group+12)=0;
            *(ushort*)(group+0x2c)=0x100;
            *(uint*)(group+0x28)=(uint)Kernel; *(uint*)(group+0x34)=(uint)(Memory.Address+0x6000);
            *(int*)(Memory.Address+0x6000)=0;
            nint data=Memory.Address+0x4000;
            *(uint*)(Object+0x100)=(uint)data;
            *(uint*)(Kernel+12)=(uint)FhUtil.ptr_at<byte>(0x342cc0);
            *(uint*)(data+4)=1; *(int*)(data+8)=1;
            *(uint*)(Object+0xe0)=(uint)(Memory.Address+0x7000);
            *(uint*)(Object+0xe4)=(uint)(Memory.Address+0x8800);
            *(uint*)(Object+0xf0)=(uint)(Memory.Address+0x8000); *(int*)(Object+0xf4)=2;
            *(byte*)(Memory.Address+0x7000)=1; *(int*)(Memory.Address+0x7008)=1;
            FhUtil.set_at(EngineAddresses.PpvFpGroupTable,Memory.Address+0x9000);
            *(ushort*)(Memory.Address+0x9004)=1;
        }
        public void Dispose()=>Memory.Dispose();
    }

    [Fact]
    public void ActualDispatcherPublishesFreshGeometryWithoutRepeatingSimulation()
    {
        using var f=new Fixture();
        int simulation=0;
        PilotNative.OnDispatch=_=>simulation++;
        f.Module.DispatchField(f.Manager);
        Assert.True(f.Module.FieldQualified());
        f.Module.LiveManagers = true;
        f.Module.RunWorker(0,0);
        FhUtil.set_at(0x88f508, 128);
        Assert.True(f.Module.QualifyPilot());
        byte[] before=new ReadOnlySpan<byte>((void*)f.Object,0x104).ToArray();
        byte[] children=new ReadOnlySpan<byte>((void*)(f.Memory.Address+0x7000),0x1100).ToArray();
        RenderPassContext.IsPresentation=true;
        PilotNative.OnDraw=obj=> { Assert.Equal(1,FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF)); *(int*)(obj+0x40)=222; };
        for(int i=0;i<5;i++) f.Module.DispatchField(f.Manager);
        Assert.Equal(1,simulation); Assert.Equal(5,PilotNative.PacketPublications);
        Assert.Equal(before,new ReadOnlySpan<byte>((void*)f.Object,0x104).ToArray());
        Assert.Equal(children,new ReadOnlySpan<byte>((void*)(f.Memory.Address+0x7000),0x1100).ToArray());
        Assert.Equal(0,FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF));
        f.Module.DeleteSprite(f.Object);
        Assert.False(f.Module.FieldQualified());
        f.Module.DispatchField(f.Manager);
        Assert.Equal(5,PilotNative.PacketPublications);
    }

    [Fact]
    public void ActualQualificationRejectsChangedCallbackAndBattleManager()
    {
        using var f=new Fixture();
        f.Module.DispatchField(f.Manager);
        Assert.True(f.Module.FieldQualified());
        *(uint*)(f.Kernel+0x14)=123;
        Assert.False(f.Module.FieldQualified());
        *(uint*)(f.Kernel+0x14)=0;
        FhUtil.set_at(EngineAddresses.PpvPartManagerCount,1);
        *(uint*)(FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers)+0x30)=1;
        Assert.False(f.Module.FieldQualified());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void ActualOuterOwnerFreezesRestartAndManagerWorkEvenWhenNativeThrows(int countdown)
    {
        using var f=new Fixture();
        f.Module.WithoutRestartHold();
        *(int*)f.Manager=countdown;
        byte[] before=new ReadOnlySpan<byte>((void*)(f.Manager+0x40),0x40).ToArray();
        RenderPassContext.IsPresentation=true;
        PilotNative.OnFieldLoop=()=> { (*(int*)f.Manager)--; *(int*)(f.Manager+0x40)=456; throw new InvalidOperationException("native test failure"); };
        Assert.Throws<InvalidOperationException>(()=>f.Module.OuterField());
        Assert.Equal(countdown,*(int*)f.Manager);
        Assert.Equal(before,new ReadOnlySpan<byte>((void*)(f.Manager+0x40),0x40).ToArray());
    }

    [Theory]
    [InlineData(0x317010)]
    [InlineData(0x341b90)]
    [InlineData(0x334170)]
    public void EveryNewBindingRejectsBeforeAnyHook(int rva)
    {
        FhUtil.BadPrefix=rva;
        Assert.False(new VrrModule().InitializePilot());
        Assert.Empty(PilotNative.HookTargets);
    }
    [Fact]
    public void StartupRegistersHooksWithoutResolvingOrInvokingNativeCalls()
    {
        PilotNative.Initializing = true;
        try
        {
            Assert.True(new VrrModule().InitializePilot());
            Assert.Empty(PilotNative.Resolutions);
        }
        finally { PilotNative.Initializing = false; }
    }

    [Fact]
    public void RuntimeResolvesEachUsedFieldContinuationOnce()
    {
        using var f = new Fixture();
        Assert.Empty(PilotNative.Resolutions);
        f.Module.DispatchField(f.Manager);
        for (int i = 0; i < 10; i++) Assert.True(f.Module.FieldQualified());
        RenderPassContext.IsPresentation = true;
        for (int i = 0; i < 10; i++) f.Module.DispatchField(f.Manager);
        foreach (nint target in new nint[] { 0x317010, 0x241da0, 0x2453d0, 0x341b90 })
            Assert.Equal(1, PilotNative.Resolutions.GetValueOrDefault(target));
        foreach (nint unused in new nint[] { 0x32d390, 0x334170, 0x334bd0, 0x340a70, 0x340f60 })
            Assert.False(PilotNative.Resolutions.ContainsKey(unused));
    }}
