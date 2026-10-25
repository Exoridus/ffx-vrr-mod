using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Particle presentation")]
public unsafe sealed class FieldParticlePlanTests
{
    private sealed class Fixture : IDisposable
    {
        internal readonly PilotMemory Memory = new();
        internal nint Manager => Memory.Address + 0x400;
        internal nint Object => Memory.Address + 0x1000;
        internal nint Kernel => Memory.Address + 0x5000;
        internal nint Data => Memory.Address + 0x4000;
        internal readonly Dictionary<nint, FieldParticleKernel> Policies = [];
        internal Fixture()
        {
            nint entry = Memory.Address + 0x2000, group = Memory.Address + 0x3000;
            new Span<byte>((void*)Manager, 0x80).Clear();
            *(int*)(Manager+0x18)=1; *(uint*)(Manager+0x3c)=(uint)entry;
            *(uint*)entry=(uint)group; *(uint*)(entry+4)=(uint)Object; *(ushort*)(entry+12)=1;
            *(uint*)Object=0; *(uint*)(Object+8)=(uint)entry;
            *(int*)(group+0x20)=0x100; *(short*)(group+0x26)=1; *(byte*)(group+12)=0;
            *(ushort*)(group+0x2c)=0x100;
            *(uint*)(group+0x28)=(uint)Kernel; *(uint*)(group+0x34)=(uint)(Memory.Address+0x6000);
            *(int*)(Memory.Address+0x6000)=0;
            *(uint*)(Object+0x100)=(uint)Data;
            *(uint*)(Kernel+12)=0x342cc0;
            Policies[Kernel]=new(0,0x342cc0,0,FieldParticleProducer.ShapeField,0x341b90,0x100);
            *(uint*)(Data+4)=1; *(int*)(Data+8)=1;
            *(uint*)(Object+0xe0)=(uint)(Memory.Address+0x7000);
            *(uint*)(Object+0xe4)=(uint)(Memory.Address+0x8800);
            *(uint*)(Object+0xf0)=(uint)(Memory.Address+0x8000);
            *(int*)(Object+0xf4)=2;
            *(byte*)(Memory.Address+0x7000)=1; *(int*)(Memory.Address+0x7008)=1;
        }
        public void Dispose()=>Memory.Dispose();
    }

    [Fact]
    public void DedicatedGeometryKeepsCanonicalObjectChildrenAndHistory()
    {
        using var f=new Fixture();
        Assert.True(FieldParticlePresentationPlan.TryBuild(f.Manager,7,f.Policies,out var plan));
        Assert.Single(plan!.Calls);
        Assert.Equal(f.Object+0xa0,plan.Calls[0].Private);
        byte[] before=new ReadOnlySpan<byte>((void*)f.Object,0x104).ToArray();
        byte[] external=new ReadOnlySpan<byte>((void*)(f.Memory.Address+0x7000),0x1100).ToArray();
        int geometry=0;
        plan.Replay(call=> { geometry++; Assert.Equal((nint)0x341b90,call.Kernel.Target); *(int*)(call.Object+0x40)=123; },()=>true);
        Assert.Equal(1,geometry);
        Assert.Equal(before,new ReadOnlySpan<byte>((void*)f.Object,0x104).ToArray());
        Assert.Equal(external,new ReadOnlySpan<byte>((void*)(f.Memory.Address+0x7000),0x1100).ToArray());
        Assert.True(plan.Matches(7,f.Policies)); Assert.False(plan.Matches(8,f.Policies));
    }
    [Fact]
    public void UnknownProducerOrManagerCallbacksRejectWholePlan()
    {
        using var f=new Fixture();
        *(uint*)(f.Kernel+12)=0x342123;
        Assert.False(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out _));
        *(uint*)(f.Kernel+12)=0x342cc0; *(uint*)(f.Kernel+0x14)=0x123456;
        Assert.False(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out _));
    }
    [Fact]
    public void ChangedProgramOrChildExtentCannotReplay()
    {
        using var f=new Fixture();
        Assert.True(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out var plan));
        *(int*)(f.Memory.Address+0x7008)=2;
        Assert.False(plan!.Matches(1,f.Policies));
        *(int*)(f.Memory.Address+0x7008)=1;
        *(uint*)(f.Object+0x100)=(uint)(f.Data+0x100);
        Assert.False(plan.Matches(1,f.Policies));
    }
    [Fact]
    public void LifecycleChangeNeverRestoresBytesIntoReusedObject()
    {
        using var f=new Fixture();
        Assert.True(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out var plan));
        Assert.Throws<InvalidOperationException>(()=>plan!.Replay(call=>*(int*)(call.Object+0x40)=456,()=>false));
        Assert.Equal(456,*(int*)(f.Object+0x40));
    }
    [Fact]
    public void MissingAuxiliaryAllocationRejectsPartiallyInitializedChildren()
    {
        using var f=new Fixture();
        *(uint*)(f.Object+0xe4)=0;
        Assert.False(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out _));
    }
    [Fact]
    public void PositionPrivateOffsetOutsideObjectRejectsBeforeNativeGeometry()
    {
        using var f=new Fixture();
        *(int*)(f.Memory.Address+0x6008)=0x100;
        Assert.False(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out _));
    }    [Fact]
    public void NarrowFieldOperandRowNeverUsesFullGeometryContract()
    {
        using var f=new Fixture();
        *(ushort*)(f.Memory.Address+0x302c)=0xb0;
        Assert.False(FieldParticlePresentationPlan.TryBuild(f.Manager,1,f.Policies,out _));
    }}
