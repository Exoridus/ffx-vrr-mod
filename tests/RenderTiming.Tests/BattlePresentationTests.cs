using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class BattlePresentationTests
{
    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, true, false, false)]
    [InlineData(true, true, false, true, false)]
    public void Actual_adapter_rejects_inactive_owner_active_overlay_and_teardown(bool pool, bool owned, bool active, bool changed, bool expected)
    {
        NativeActorPool.Reset();
        NativeActorPool.Pointer = pool ? 0x100000 : 0;
        if (active) NativeActorPool.Bytes[30 * 0xf90 + 0xdfb] = 5;
        if (changed) NativeActorPool.AfterCopy = () => NativeActorPool.Pointer = 0x200000;
        try
        {
            Assert.Equal(expected, new VrrModule().EnableAdapter().ProbeBattlePool(owned ? Environment.CurrentManagedThreadId : 0));
        }
        finally { NativeActorPool.Reset(); }
    }

    [Fact]
    public void Another_thread_does_not_read_the_actor_pool()
    {
        NativeActorPool.Reset();
        NativeActorPool.Pointer = 0x100000;
        try
        {
            Assert.False(new VrrModule().EnableAdapter().ProbeBattlePool(Environment.CurrentManagedThreadId + 1));
            Assert.Equal(0, NativeActorPool.CopyCount);
        }
        finally { NativeActorPool.Reset(); }
    }

    [Fact]
    public void Failed_memory_read_rejects_the_image()
    {
        NativeActorPool.Reset();
        NativeActorPool.Pointer = 0x100000;
        NativeActorPool.CopySucceeded = false;
        try { Assert.False(new VrrModule().EnableAdapter().ProbeBattlePool(Environment.CurrentManagedThreadId)); }
        finally { NativeActorPool.Reset(); }
    }
}

public unsafe sealed partial class VrrModule
{
    internal bool ProbeBattlePool(int owner)
    {
        _render_owner_thread = owner;
        return battle_effect_presentation_is_qualified();
    }
}

internal static class NativeActorPool
{
    internal static nint Pointer;
    internal static readonly byte[] Bytes = new byte[0x1e270];
    internal static Action? AfterCopy;
    internal static int CopyCount;
    internal static bool CopySucceeded = true;

    internal static void Reset()
    {
        Pointer = 0;
        Array.Clear(Bytes);
        AfterCopy = null;
        CopyCount = 0;
        CopySucceeded = true;
    }
}

internal static class MemoryProbe
{
    internal static bool IsReadable(nint address, int length) => address != 0 && length > 0;
    internal static bool TryCopy(nint address, Span<byte> target)
    {
        NativeActorPool.CopyCount++;
        if (!NativeActorPool.CopySucceeded || address != NativeActorPool.Pointer) return false;
        NativeActorPool.Bytes.AsSpan().CopyTo(target);
        NativeActorPool.AfterCopy?.Invoke();
        return true;
    }
}
