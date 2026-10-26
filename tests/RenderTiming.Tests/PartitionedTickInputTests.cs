using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class PartitionedTickInputTests
{
    [Fact]
    public void Conservative_observation_boundary_selects_the_same_tick_across_image_partitions()
    {
        var grouped = Run([10.25], false);
        var split = Run([10.017, 10.25], false);
        AssertEquivalent(grouped, split);
        Assert.Empty(grouped[5].Input.Observations);
        Assert.Equal(1, Assert.Single(grouped[6].Input.Observations).PollSequence);
    }

    [Fact]
    public void Both_ports_keep_down_up_order_and_analog_state_across_image_partitions()
    {
        var grouped = Run([10.25], true);
        var split = Run([10.001, 10.017, 10.25], true);
        AssertEquivalent(grouped, split);
        Assert.Empty(grouped[5].Input.Observations);
        Assert.Equal(new long[] { 1, 1, 2, 2 }, grouped[6].Input.Observations.Select(s => s.PollSequence));
        Assert.Equal(new uint[] { 1, 1, 0, 0 }, grouped[6].Input.Observations.Select(s => s.RawWords[6]));
        Assert.Equal(new uint[] { 100, 101, 200, 201 }, grouped[6].Input.Observations.Select(s => s.RawWords[1]));
        Assert.All(grouped[6].Input.Latest, sample => Assert.Equal(2, sample!.PollSequence));
    }

    private static List<(double Deadline, PadTickCorrelation Input)> Run(double[] images, bool bothPorts)
    {
        var clock = new FixedSimulationClock();
        var timeline = new PadObservationTimeline(10_000_000);
        for (int port = 0; port < (bothPorts ? 2 : 1); port++)
        {
            uint[] words = new uint[8];
            words[1] = (uint)(100 + port);
            words[6] = 1;
            Assert.True(timeline.Observe(1, port, 100_999_999, 101_000_000, words));
        }
        if (bothPorts)
            for (int port = 0; port < 2; port++)
            {
                uint[] words = new uint[8];
                words[1] = (uint)(200 + port);
                Assert.True(timeline.Observe(2, port, 101_000_000, 101_000_001, words));
            }
        clock.Plan(10);
        List<(double, PadTickCorrelation)> result = [];
        foreach (double now in images)
        {
            var plan = clock.Plan(now);
            Assert.True(clock.HasMappedInterval);
            for (int i = 0; i < plan.Steps; i++)
            {
                Assert.True(clock.TryGetStepDeadline(i, out double deadline));
                result.Add((deadline, timeline.Correlate(deadline)));
            }
        }
        Assert.Equal(15, result.Count);
        return result;
    }

    private static void AssertEquivalent(List<(double Deadline, PadTickCorrelation Input)> grouped,
        List<(double Deadline, PadTickCorrelation Input)> split)
    {
        Assert.Equal(grouped.Count, split.Count);
        for (int tick = 0; tick < grouped.Count; tick++)
        {
            var a = grouped[tick];
            var b = split[tick];
            Assert.Equal(a.Input.Observations.Select(s => (s.PollSequence, s.Port)),
                b.Input.Observations.Select(s => (s.PollSequence, s.Port)));
            for (int port = 0; port < 2; port++)
            {
                Assert.Equal(a.Input.Latest[port]?.PollSequence, b.Input.Latest[port]?.PollSequence);
                Assert.Equal(a.Input.Latest[port]?.RawWords, b.Input.Latest[port]?.RawWords);
            }
        }
        Assert.Equal(grouped.Select(tick => tick.Deadline), split.Select(tick => tick.Deadline));
    }
}
