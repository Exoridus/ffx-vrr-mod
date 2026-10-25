using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class BattleEffectStateTests
{
    [Fact]
    public void Empty_complete_actor_pool_has_no_active_overlay()
    {
        Assert.True(BattleEffectState.CanPresent(new byte[0x1e270]));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Every_native_slot_rejects_active_overlay_state(byte state)
    {
        byte[] actors = new byte[0x1e270];
        for (int slot = 0; slot < 31; slot++)
        {
            actors[slot * 0xf90 + 0xdfb] = state;
            Assert.False(BattleEffectState.CanPresent(actors));
            actors[slot * 0xf90 + 0xdfb] = 0;
        }
    }

    [Fact]
    public void Only_native_overlay_states_block_the_complete_pool()
    {
        byte[] actors = new byte[0x1e270];
        for (int state = 0; state <= byte.MaxValue; state++)
        {
            actors[30 * 0xf90 + 0xdfb] = (byte)state;
            Assert.Equal(state is not (4 or 5), BattleEffectState.CanPresent(actors));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xf90)]
    [InlineData(0x1e26f)]
    public void Incomplete_snapshots_do_not_qualify(int length)
    {
        Assert.False(BattleEffectState.CanPresent(new byte[length]));
    }
}
