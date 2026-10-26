using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class PadObservationTimelineTests
{
    private static uint[] Words(uint buttons = 0) => [0x05020101, 1, 2, 3, 4, 0x0005000c, buttons, 0x05010000];
    private static double After(long ticks, long frequency = 1000) => Math.BitIncrement(ticks / (double)frequency);

    [Fact]
    public void Keeps_all_down_and_up_observations_and_latest_candidate()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 9, 10, Words(1)));
        Assert.True(timeline.Observe(2, 0, 19, 20, Words(0)));
        var result = timeline.Correlate(After(20));
        Assert.Equal(new long[] { 1, 2 }, result.Observations.Select(o => o.PollSequence));
        Assert.Equal(new uint[] { 1, 0 }, result.Observations.Select(o => o.RawWords[6]));
        Assert.Equal(2, result.Latest[0]!.PollSequence);
        Assert.Null(result.Latest[1]);
        Assert.Empty(timeline.Correlate(After(30)).Observations);
        Assert.Equal(2, timeline.Correlate(After(40)).Latest[0]!.PollSequence);
    }

    [Fact]
    public void Copy_end_is_conservative_and_future_samples_stay_pending()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 1, 20, Words(1)));
        Assert.Empty(timeline.Correlate(.01).Observations);
        Assert.Empty(timeline.Correlate(.02).Observations);
        Assert.Single(timeline.Correlate(After(20)).Observations);
    }

    [Fact]
    public void Port_timestamps_remain_separate_and_eligible_samples_keep_arrival_order()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 20, 30, Words(1)));
        Assert.True(timeline.Observe(1, 1, 10, 11, Words(2)));
        Assert.True(timeline.Observe(2, 1, 12, 13, Words(3)));
        var first = timeline.Correlate(After(15));
        Assert.Equal(new int[] { 1, 1 }, first.Observations.Select(o => o.Port));
        Assert.Null(first.Latest[0]);
        Assert.Equal(2, first.Latest[1]!.PollSequence);
        var later = timeline.Correlate(After(30));
        Assert.Equal(0, Assert.Single(later.Observations).Port);
        Assert.Equal(1, later.Latest[0]!.PollSequence);
    }

    [Fact]
    public void Equal_copy_times_retain_sequence_order()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 10, 10, Words(1)));
        Assert.True(timeline.Observe(2, 0, 10, 10, Words(0)));
        Assert.Equal(new long[] { 1, 2 }, timeline.Correlate(After(10)).Observations.Select(o => o.PollSequence));
    }

    [Fact]
    public void Same_observed_timeline_has_identical_assignments_across_image_partitions()
    {
        static long[][] Run(bool incremental)
        {
            var timeline = new PadObservationTimeline(1000);
            var samples = new (long Time, uint Buttons)[] { (3, 1), (7, 0), (14, 2), (14, 0), (25, 3) };
            int next = 0;
            if (!incremental)
                while (next < samples.Length)
                {
                    var sample = samples[next];
                    Assert.True(timeline.Observe(++next, 0, sample.Time, sample.Time, Words(sample.Buttons)));
                }
            List<long[]> assigned = [];
            foreach (long deadline in new long[] { 10, 20, 30 })
            {
                if (incremental)
                    while (next < samples.Length && samples[next].Time <= deadline)
                    {
                        var sample = samples[next];
                        Assert.True(timeline.Observe(++next, 0, sample.Time, sample.Time, Words(sample.Buttons)));
                    }
                assigned.Add(timeline.Correlate(After(deadline)).Observations.Select(o => o.PollSequence).ToArray());
            }
            return assigned.ToArray();
        }
        var batched = Run(false);
        var incremental = Run(true);
        Assert.Equal(batched.Length, incremental.Length);
        for (int i = 0; i < batched.Length; i++) Assert.Equal(batched[i], incremental[i]);
        Assert.Equal(new long[] { 1, 2 }, batched[0]);
        Assert.Equal(new long[] { 3, 4 }, batched[1]);
    }

    [Theory]
    [InlineData(0, 1u)]
    [InlineData(5, 1u)]
    [InlineData(7, 0x00010000u)]
    [InlineData(7, 0x01000000u)]
    public void Observed_status_repeat_or_enable_changes_start_unknown_epoch(int word, uint change)
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 1, 2, Words(1)));
        Assert.True(timeline.Observe(1, 1, 1, 2, Words(2)));
        Assert.NotNull(timeline.Correlate(After(2)).Latest[1]);
        uint[] changed = Words(0);
        changed[word] ^= change;
        long epoch = timeline.Epoch;
        Assert.True(timeline.Observe(2, 0, 3, 4, changed));
        Assert.Equal(epoch + 1, timeline.Epoch);
        var result = timeline.Correlate(After(4));
        Assert.Equal("observed_status_change", result.Boundary);
        Assert.Equal(2, Assert.Single(result.Observations).PollSequence);
        Assert.Null(result.Latest[1]);
    }

    [Fact]
    public void Native_head_and_actuator_changes_do_not_claim_a_status_boundary()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 1, 2, Words(1)));
        uint[] changed = Words(0);
        changed[7] ^= 0xffff;
        Assert.True(timeline.Observe(2, 0, 3, 4, changed));
        Assert.Equal(0, timeline.Epoch);
        Assert.Equal(2, timeline.Correlate(After(4)).Observations.Length);
    }

    [Theory]
    [InlineData(0, 0, 1, 2, 8)]
    [InlineData(-1, 0, 1, 2, 8)]
    [InlineData(1, -1, 1, 2, 8)]
    [InlineData(1, 2, 1, 2, 8)]
    [InlineData(1, 0, -1, 2, 8)]
    [InlineData(1, 0, 3, 2, 8)]
    [InlineData(1, 0, 1, 2, 7)]
    [InlineData(1, 0, 1, 2, 9)]
    [InlineData(1, 0, 1, 9007199254740993L, 8)]
    public void Invalid_observation_refuses_and_clears_prior_state(long sequence, int port, long start, long end, int length)
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 1, 0, 0, Words(7)));
        Assert.NotNull(timeline.Correlate(.001).Latest[1]);
        Assert.False(timeline.Observe(sequence, port, start, end, new uint[length]));
        Assert.Equal(1, timeline.Epoch);
        var result = timeline.Correlate(1);
        Assert.Equal("invalid_observation", result.Boundary);
        Assert.Empty(result.Observations);
        Assert.All(result.Latest, Assert.Null);
    }

    [Theory]
    [InlineData(1, 3, 4)]
    [InlineData(0, 3, 4)]
    [InlineData(2, 1, 4)]
    [InlineData(2, 1, 1)]
    public void Per_port_order_violation_invalidates_pending_samples(long sequence, long start, long end)
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 1, 2, Words()));
        Assert.False(timeline.Observe(sequence, 0, start, end, Words()));
        Assert.Equal(1, timeline.Epoch);
        Assert.Empty(timeline.Correlate(1).Observations);
    }

    [Fact]
    public void Overflow_is_explicit_and_does_not_silently_drop_old_input()
    {
        var timeline = new PadObservationTimeline(1000, 2);
        Assert.True(timeline.Observe(1, 0, 1, 1, Words(1)));
        Assert.True(timeline.Observe(1, 1, 1, 1, Words(2)));
        Assert.False(timeline.Observe(2, 0, 2, 2, Words(0)));
        Assert.Equal("observation_overflow", timeline.Boundary);
        Assert.Empty(timeline.Correlate(1).Observations);
        Assert.All(timeline.Correlate(2).Latest, Assert.Null);
    }

    [Fact]
    public void Returned_payloads_and_original_buffers_cannot_mutate_retained_state()
    {
        var timeline = new PadObservationTimeline(1000);
        uint[] words = Words(1);
        Assert.True(timeline.Observe(1, 0, 1, 1, words));
        words[6] = 999;
        var first = timeline.Correlate(After(1));
        Assert.Equal(1u, first.Observations[0].RawWords[6]);
        first.Observations[0].RawWords[6] = 99;
        Assert.Equal(1u, first.Latest[0]!.RawWords[6]);
        first.Latest[0]!.RawWords[6] = 88;
        first.Latest[0] = null;
        Assert.Equal(1u, timeline.Correlate(After(2)).Latest[0]!.RawWords[6]);
    }

    [Fact]
    public void Late_arriving_observation_refuses_retrospective_assignment()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 1, 1, 1, Words(1)));
        Assert.NotNull(timeline.Correlate(After(10)).Latest[1]);
        Assert.False(timeline.Observe(2, 0, 2, 3, Words(0)));
        Assert.Equal("late_observation", timeline.Boundary);
        Assert.Equal(1, timeline.Epoch);
        var result = timeline.Correlate(1);
        Assert.Empty(result.Observations);
        Assert.All(result.Latest, Assert.Null);
    }
    [Fact]
    public void Explicit_reset_clears_every_candidate_and_accepts_a_new_clock_origin()
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(8, 0, 200, 300, Words(1)));
        Assert.NotNull(timeline.Correlate(1).Latest[0]);
        timeline.Reset("native_owner");
        Assert.Equal(1, timeline.Epoch);
        Assert.Equal("native_owner", timeline.Boundary);
        Assert.True(timeline.Observe(1, 0, 0, 0, Words(0)));
        Assert.Equal(1, Assert.Single(timeline.Correlate(.001).Observations).PollSequence);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(.5)]
    [InlineData(1)]
    public void Invalid_or_nonincreasing_deadline_refuses_with_unknown_state(double deadline)
    {
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, 1, 1, Words(1)));
        Assert.NotNull(timeline.Correlate(1).Latest[0]);
        var refused = timeline.Correlate(deadline);
        Assert.Equal("invalid_deadline", refused.Boundary);
        Assert.Equal(1, refused.Epoch);
        Assert.Empty(refused.Observations);
        Assert.All(refused.Latest, Assert.Null);
    }

    [Fact]
    public void Largest_exact_integer_tick_is_preserved_without_future_assignment()
    {
        const long end = 1L << 53;
        var timeline = new PadObservationTimeline(1000);
        Assert.True(timeline.Observe(1, 0, end, end, Words()));
        Assert.Empty(timeline.Correlate(end / 1000d).Observations);
        Assert.Single(timeline.Correlate(After(end)).Observations);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(9007199254740993L, 1)]
    [InlineData(1000, 0)]
    [InlineData(1000, -1)]
    public void Invalid_constructor_bounds_are_rejected(long frequency, int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PadObservationTimeline(frequency, capacity));
    }
}
