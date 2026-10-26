using Xunit;
using static Fahrenheit.Mods.Vrr.BattleEffectPauseCode;

namespace Fahrenheit.Mods.Vrr;

public sealed class OpuRepeatTrackerTests
{
    [Fact]
    public void Learned_repeat_does_not_admit_a_changed_pc()
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        Assert.True(tracker.Entry(3, 1, 0, 0x200, 0, 7));
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
    }

    [Fact]
    public void Repeat_permission_survives_held_images_in_the_same_epoch()
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        Assert.False(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        Assert.False(tracker.Entry(3, 1, 0, 0x100, 0, 7));
    }

    [Fact]
    public void Changed_epoch_discards_learned_and_pending_observations()
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 8));
        tracker.Entry(0, 1, 0, 0x100, 0, 8);
        tracker.Next(1, 0, 0x100, 9);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 9));
    }

    [Fact]
    public void Normal_entry_revokes_permission_even_when_next_is_missing()
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        tracker.Entry(0, 1, 0, 0x100, 0, 7);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
    }

    [Theory]
    [InlineData(0x200u, (byte)0)]
    [InlineData(0x100u, (byte)1)]
    public void Nonrepeat_next_revokes_previous_permission(uint nextPc, byte wait)
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        tracker.Entry(0, 1, 0, 0x100, wait, 7);
        tracker.Next(1, 0, nextPc, 7);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(1, 1)]
    public void Mismatched_next_cannot_learn_or_leave_pending_observation(int record, int stream)
    {
        var tracker = new OpuRepeatTracker();
        tracker.Entry(0, 1, 0, 0x100, 0, 7);
        tracker.Next(record, stream, 0x100, 7);
        tracker.Next(1, 0, 0x100, 7);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
    }

    [Fact]
    public void Nested_observation_cannot_complete_the_previous_stream()
    {
        var tracker = new OpuRepeatTracker();
        tracker.Entry(0, 1, 0, 0x100, 0, 7);
        tracker.Entry(0, 2, 1, 0x100, 0, 7);
        tracker.Next(1, 0, 0x100, 7);
        tracker.Next(2, 1, 0x100, 7);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        Assert.True(tracker.Entry(3, 2, 1, 0x100, 0, 7));
    }

    [Theory]
    [InlineData(0, 0, 0x100u)]
    [InlineData(1, -1, 0x100u)]
    [InlineData(1, 4, 0x100u)]
    [InlineData(1, 0, 0u)]
    public void Invalid_identity_cannot_learn_or_admit(int record, int stream, uint pc)
    {
        var tracker = new OpuRepeatTracker();
        tracker.Entry(0, record, stream, pc, 0, 7);
        tracker.Next(record, stream, pc, 7);
        Assert.True(tracker.Entry(3, record, stream, pc, 0, 7));
    }

    [Fact]
    public void Clear_discards_both_learned_and_pending_state_without_resetting_counters()
    {
        var tracker = new OpuRepeatTracker();
        Learn(tracker);
        Assert.False(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        tracker.Entry(0, 2, 0, 0x100, 0, 7);
        tracker.Clear();
        tracker.Next(2, 0, 0x100, 7);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        Assert.True(tracker.Entry(3, 2, 0, 0x100, 0, 7));
        Assert.Equal(1, tracker.Ran);
    }

    [Fact]
    public void Permission_is_per_record_and_stream_and_limit_counts_records()
    {
        var tracker = new OpuRepeatTracker();
        for (int record = 1; record <= 4096; record++)
            for (int stream = 0; stream < 4; stream++) Learn(tracker, record, stream);
        Assert.False(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        Learn(tracker, 4097);
        Assert.True(tracker.Entry(3, 1, 0, 0x100, 0, 7));
        Assert.False(tracker.Entry(3, 4097, 0, 0x100, 0, 7));
        Assert.True(tracker.Entry(3, 4097, 1, 0x100, 0, 7));
    }

    private static void Learn(OpuRepeatTracker tracker, nint record = 1, int stream = 0)
    {
        Assert.False(tracker.Entry(0, record, stream, 0x100, 0, 7));
        tracker.Next(record, stream, 0x100, 7);
    }
}
