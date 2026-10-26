using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class OpuRepeatBridgeTests
{
    [Fact]
    public void Real_callbacks_keep_same_pc_permission_and_repair_only_the_skipped_second_sweep_latch()
    {
        using var fixture = new Fixture();
        fixture.Learn();
        fixture.Hold = 3;
        Assert.Equal(0, fixture.Entry());
        fixture.Next();
        Assert.Equal(0, fixture.Entry());
        fixture.Pc = 0x200;
        fixture.Latch = 0x80;
        Assert.Equal(1, fixture.Entry());
        Assert.Equal(0x80, fixture.Latch);
        fixture.Sweep = 1;
        Assert.Equal(1, fixture.Entry());
        Assert.Equal(0x81, fixture.Latch);
    }

    [Fact]
    public void Real_callbacks_use_the_selected_stream_pc_wait_and_latch_bit()
    {
        using var fixture = new Fixture();
        fixture.Stream = 2;
        fixture.Pc = 0x200;
        fixture.Learn();
        fixture.Hold = 3;
        Assert.Equal(0, fixture.Entry());
        fixture.Stream = 0;
        Assert.Equal(1, fixture.Entry());
        fixture.Stream = 2;
        fixture.Wait = 1;
        fixture.Pc = 0x300;
        Assert.Equal(0, fixture.Entry());
        fixture.Wait = 0;
        fixture.Sweep = 1;
        fixture.Latch = 0x80;
        Assert.Equal(1, fixture.Entry());
        Assert.Equal(0x84, fixture.Latch);
    }

    [Fact]
    public void Real_callbacks_read_current_history_at_both_boundaries()
    {
        using var fixture = new Fixture();
        fixture.Learn();
        RenderPassContext.ResetHistory();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
        fixture.Hold = 0;
        Assert.Equal(0, fixture.Entry());
        RenderPassContext.ResetHistory();
        fixture.Next();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
    }

    [Fact]
    public void Real_normal_entry_and_clear_revoke_learned_or_pending_permission()
    {
        using var fixture = new Fixture();
        fixture.Learn();
        Assert.Equal(0, fixture.Entry());
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
        fixture.Hold = 0;
        fixture.Learn();
        fixture.Module.ClearOpuRepeat();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
        fixture.Hold = 0;
        Assert.Equal(0, fixture.Entry());
        fixture.Module.ClearOpuRepeat();
        fixture.Next();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
    }

    [Fact]
    public void Real_nested_stream_observation_does_not_pair_with_an_earlier_next()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, fixture.Entry());
        fixture.Stream = 1;
        Assert.Equal(0, fixture.Entry());
        fixture.Stream = 0;
        fixture.Next();
        fixture.Stream = 1;
        fixture.Next();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
        fixture.Stream = 0;
        Assert.Equal(1, fixture.Entry());
    }

    [Fact]
    public void Foreign_thread_callbacks_keep_native_fallback_and_cannot_learn()
    {
        using var fixture = new Fixture();
        int result = -1;
        var thread = new Thread(() => { result = fixture.Entry(); fixture.Next(); });
        thread.Start();
        thread.Join();
        Assert.Equal(0, result);
        fixture.Hold = 3;
        fixture.Sweep = 1;
        Assert.Equal(1, fixture.Entry());
        fixture.Module.ClearOpuRepeat();
        fixture.Hold = 0;
        Assert.Equal(0, fixture.Entry());
        var nextThread = new Thread(fixture.Next);
        nextThread.Start();
        nextThread.Join();
        fixture.Hold = 3;
        Assert.Equal(1, fixture.Entry());
        fixture.Learn();
        fixture.Hold = 3;
        fixture.Latch = 0;
        var heldThread = new Thread(() => result = fixture.Entry());
        heldThread.Start();
        heldThread.Join();
        Assert.Equal(0, result);
        Assert.Equal(0, fixture.Latch);
    }

    [Theory]
    [InlineData((byte)0, (byte)0)]
    [InlineData((byte)1, (byte)0)]
    [InlineData((byte)2, (byte)0)]
    [InlineData((byte)4, (byte)0)]
    [InlineData((byte)3, (byte)1)]
    public void Real_callbacks_preserve_native_decision_outside_held_wait_zero(byte hold, byte wait)
    {
        using var fixture = new Fixture();
        fixture.Hold = hold;
        fixture.Wait = wait;
        fixture.Sweep = 1;
        Assert.Equal(0, fixture.Entry());
        Assert.Equal(0, fixture.Latch);
    }

    [Fact]
    public void Missing_module_keeps_native_fallback()
    {
        using var fixture = new Fixture();
        VrrModule.ReleaseOpuOwner();
        Assert.Equal(0, fixture.Entry());
        fixture.Next();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly nint _scratch = (nint)NativeMemory.AllocZeroed(0x220);
        private readonly nint _record = (nint)NativeMemory.AllocZeroed(0x40);
        private readonly byte* _data = (byte*)NativeMemory.AllocZeroed(0x20);
        internal readonly VrrModule Module = new();
        internal Fixture()
        {
            Module.AttachOpuOwner(_data);
            for (int stream = 0; stream < 4; stream++) *(uint*)(_record + stream * 4) = 0x100;
        }
        internal byte Hold { set => _data[1] = value; }
        internal int Stream { set => *(short*)(_scratch + 0x216) = (short)value; }
        internal uint Pc { set => *(uint*)(_record + *(short*)(_scratch + 0x216) * 4) = value; }
        internal byte Wait { set => *(byte*)(_record + 0x10 + *(short*)(_scratch + 0x216)) = value; }
        internal byte Sweep { set => *(byte*)(_scratch + 0x21d) = value; }
        internal byte Latch { get => *(byte*)(_record + 0x1f); set => *(byte*)(_record + 0x1f) = value; }
        internal int Entry() => VrrModule.OpuEntryCallback(_scratch, _record);
        internal void Next() => VrrModule.OpuNextCallback(_scratch, _record);
        internal void Learn() { Hold = 0; Assert.Equal(0, Entry()); Next(); }
        public void Dispose()
        {
            VrrModule.ReleaseOpuOwner();
            NativeMemory.Free((void*)_scratch);
            NativeMemory.Free((void*)_record);
            NativeMemory.Free(_data);
        }
    }
}

public unsafe sealed partial class VrrModule
{
    private int _particle_pilot_thread;
    internal void AttachOpuOwner(byte* data) { _bx = data; _particle_pilot_thread = Environment.CurrentManagedThreadId; s_battle_effects = this; }
    internal static void ReleaseOpuOwner() => s_battle_effects = null;
    internal void ClearOpuRepeat() => _bxRepeat.Clear();
    internal static delegate* unmanaged[Cdecl]<nint, nint, int> OpuEntryCallback => &bx_on_stream_entry;
    internal static delegate* unmanaged[Cdecl]<nint, nint, void> OpuNextCallback => &bx_on_stream_next;
}
