using System.Reflection;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class BuoyancyTests : IDisposable
{
    private readonly VrrModule _module = new();
    private readonly nint _actor = (nint)NativeMemory.AllocZeroed(0x880);

    public BuoyancyTests()
    {
        FhUtil.set_at(EngineAddresses.ChrArray, _actor);
        FhUtil.set_at(EngineAddresses.NbMaxChr, 1);
        *((byte*)_actor + 2) = 1;
        *((byte*)_actor + 0x182) = 2;
        Set(0x10, 10); Set(0x504, 2); Set(0x4fc, 3);
    }

    [Fact]
    public void Barrier_restores_height_after_wait_and_before_worker_dispatch()
    {
        Call("init_buoyancy_hook");
        Call("buoyancy_hold_begin");
        Set(0x10, 20); Set(0x504, 4); Set(0x4fc, 6); Set(0xc, 99);
        bool waited = false;
        NativeCalls.OnBuoyancyBarrier = () => { Assert.Equal(20, Get(0x10)); waited = true; };
        Call("h_buoyancy_barrier");
        Assert.True(waited);
        Assert.Equal(10, Get(0x10));
        Assert.Equal(2, Get(0x504));
        Assert.Equal(3, Get(0x4fc));
        Assert.Equal(99, Get(0xc));
        Set(0x10, 30);
        NativeCalls.OnBuoyancyBarrier = null;
        Call("h_buoyancy_barrier");
        Assert.Equal(30, Get(0x10));
    }

    [Fact]
    public void Missing_barrier_hook_never_stages_an_unsafe_height_restore()
    {
        Call("buoyancy_hold_begin");
        Set(0x10, 20);
        Call("buoyancy_hold_end");
        Assert.Equal(20, Get(0x10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Grounded_actors_keep_the_native_height(int gravityMode)
    {
        Call("init_buoyancy_hook");
        *((byte*)_actor + 0x182) = (byte)gravityMode;
        Call("buoyancy_hold_begin");
        Set(0x10, 20);
        Call("h_buoyancy_barrier");
        Assert.Equal(20, Get(0x10));
    }

    private void Set(int offset, float value) => *(float*)(_actor + offset) = value;
    private float Get(int offset) => *(float*)(_actor + offset);
    private void Call(string name) => typeof(VrrModule).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_module, null);
    public void Dispose() { NativeCalls.OnBuoyancyBarrier = null; NativeMemory.Free((void*)_actor); }
}

internal static partial class NativeCalls
{
    internal static Action? OnBuoyancyBarrier;
    public static void BuoyancyBarrier() => OnBuoyancyBarrier?.Invoke();
}

internal static partial class EngineAddresses
{
    public const nint ChrArray = 0x30600;
    public const nint NbMaxChr = 0x30610;
    public const nint CharacterJobBarrier = 0x30620;
}