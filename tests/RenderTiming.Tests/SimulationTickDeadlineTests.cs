using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class SimulationTickDeadlineTests
{
    public static IEnumerable<object[]> RatesAndSpeeds()
    {
        foreach (int rate in new[] { 30, 60, 120 })
        foreach (double speed in new[] { 1d, 0.5, 8 })
            yield return new object[] { rate, speed };
    }

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Single_and_catchup_steps_have_absolute_model_deadlines(int rate, double speed)
    {
        var clock = new FixedSimulationClock(rate);
        double interval = 1d / rate / speed;
        clock.Plan(10, speed);
        AssertUnavailable(clock, 0);
        var single = clock.Plan(10 + interval, speed);
        Assert.Equal(1, single.Steps);
        AssertDeadline(clock, 0, 10 + interval);
        var catchup = clock.Plan(10 + 4 * interval, speed);
        Assert.Equal(3, catchup.Steps);
        for (int i = 0; i < catchup.Steps; i++)
            AssertDeadline(clock, i, 10 + (i + 2) * interval);
        AssertUnavailable(clock, -1);
        AssertUnavailable(clock, catchup.Steps);
    }

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Irregular_and_grouped_images_map_the_same_model_tick_boundaries(int rate, double speed)
    {
        var fine = new FixedSimulationClock(rate);
        var grouped = new FixedSimulationClock(rate);
        fine.Plan(0, speed);
        grouped.Plan(0, speed);
        double interval = 1d / rate / speed;
        List<double> deadlines = [];
        foreach (double fraction in new[] { 0.31, 1.17, 1.92, 3.42 })
        {
            var plan = fine.Plan(fraction * interval, speed);
            for (int i = 0; i < plan.Steps; i++) deadlines.Add(ReadDeadline(fine, i));
        }
        var together = grouped.Plan(3.42 * interval, speed);
        Assert.Equal(3, together.Steps);
        Assert.Equal(together.Steps, deadlines.Count);
        for (int i = 0; i < together.Steps; i++)
        {
            Assert.Equal((i + 1) * interval, deadlines[i], 12);
            Assert.Equal(deadlines[i], ReadDeadline(grouped, i));
        }
    }

    [Theory]
    [MemberData(nameof(RatesAndSpeeds))]
    public void Epsilon_borrow_retains_signed_carry_and_a_slightly_future_deadline(int rate, double speed)
    {
        var clock = new FixedSimulationClock(rate);
        clock.Plan(0, speed);
        double interval = 1d / rate / speed;
        double now = interval - 0.5e-9 / speed;
        Assert.Equal(1, clock.Plan(now, speed).Steps);
        AssertDeadline(clock, 0, interval);
        Assert.True(ReadDeadline(clock, 0) > now);
        Assert.Equal(1, clock.Plan(2 * interval, speed).Steps);
        AssertDeadline(clock, 0, 2 * interval);
    }

    [Fact]
    public void A_speed_change_is_unmapped_then_stable_speed_recovers_with_local_phase()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0, 1);
        Assert.Equal(0, clock.Plan(0.005, 1).Steps);
        Assert.Equal(1, clock.Plan(0.015, 2).Steps);
        AssertUnavailable(clock, 0);
        Assert.Equal(1, clock.Plan(0.02, 2).Steps);
        AssertDeadline(clock, 0, 0.019166666666666667);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2)]
    [InlineData(8)]
    public void Stable_mapping_after_speed_change_keeps_exact_deadlines_across_partitions(double speed)
    {
        var grouped = new FixedSimulationClock();
        var split = new FixedSimulationClock();
        foreach (var clock in new[] { grouped, split })
        {
            clock.Plan(10);
            clock.Plan(10.005);
            clock.Plan(10.015, speed);
            Assert.False(clock.HasMappedInterval);
        }
        List<double> splitDeadlines = [];
        foreach (double now in new[] { 10.016, 10.017, 10.1 })
        {
            var plan = split.Plan(now, speed);
            Assert.True(split.HasMappedInterval);
            for (int i = 0; i < plan.Steps; i++) splitDeadlines.Add(ReadDeadline(split, i));
        }
        var together = grouped.Plan(10.1, speed);
        Assert.True(grouped.HasMappedInterval);
        Assert.Equal(together.Steps, splitDeadlines.Count);
        Assert.Equal(splitDeadlines, Enumerable.Range(0, together.Steps).Select(i => ReadDeadline(grouped, i)));
    }
    [Theory]
    [InlineData(double.NaN, 1)]
    [InlineData(double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 1)]
    [InlineData(100, 8)]
    public void Speed_normalization_is_shared_by_planning_and_deadlines(double supplied, double normalized)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0, supplied);
        Assert.Equal(1, clock.Plan(1d / 60 / normalized, normalized).Steps);
        AssertDeadline(clock, 0, 1d / 60 / normalized);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Paused_speed_has_no_deadline_and_transition_is_unmapped(double speed)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0, speed);
        Assert.Equal(0, clock.Plan(0.1, speed).Steps);
        AssertUnavailable(clock, 0);
        Assert.Equal(1, clock.Plan(0.1 + 1d / 60, 1).Steps);
        AssertUnavailable(clock, 0);
        Assert.Equal(1, clock.Plan(0.1 + 2d / 60, 1).Steps);
        AssertDeadline(clock, 0, 0.1 + 2d / 60);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.34)]
    public void Invalid_time_or_clamped_gap_invalidates_previous_deadlines(double now)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        clock.Plan(1d / 60);
        AssertDeadline(clock, 0, 1d / 60);
        Assert.True(clock.Plan(now).Reset);
        AssertUnavailable(clock, 0);
    }

    [Fact]
    public void Reset_and_a_new_empty_plan_discard_the_previous_plan_deadlines()
    {
        var clock = new FixedSimulationClock();
        AssertUnavailable(clock, 0);
        clock.Plan(0);
        clock.Plan(1d / 60);
        AssertDeadline(clock, 0, 1d / 60);
        Assert.Equal(0, clock.Plan(1d / 60).Steps);
        AssertUnavailable(clock, 0);
        clock.Plan(2d / 60);
        AssertDeadline(clock, 0, 2d / 60);
        clock.Reset();
        AssertUnavailable(clock, 0);
        Assert.True(clock.Plan(1).Reset);
        AssertUnavailable(clock, 0);
    }

    [Theory]
    [InlineData(281474976710656d, 120, 8, 1, 60)]
    [InlineData(140737488355328d, 60, 1, 4, 7)]
    public void Unrepresentable_deadlines_reject_the_whole_plan(double previous, int rate, double speed, int ulps, int steps)
    {
        var clock = new FixedSimulationClock(rate);
        clock.Plan(previous, speed);
        double now = previous;
        for (int i = 0; i < ulps; i++) now = Math.BitIncrement(now);
        var plan = clock.Plan(now, speed);
        Assert.Equal(steps, plan.Steps);
        Assert.False(plan.Reset);
        for (int i = 0; i < plan.Steps; i++) AssertUnavailable(clock, i);
    }

    [Fact]
    public void Negative_absolute_time_cannot_supply_a_model_deadline()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(-1);
        Assert.Equal(1, clock.Plan(-1 + 1d / 60).Steps);
        AssertUnavailable(clock, 0);
    }

    [Fact]
    public void Actual_render_pass_logs_each_deadline_before_decrementing_remaining()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.DeadlineImage(10);
        module.DeadlineClockAndExit();
        Assert.Null(LastPassDeadline(module));
        module.DeadlineImage(10.05);
        for (int i = 0; i < 3; i++)
        {
            module.DeadlineClockAndExit();
            Assert.Equal(10 + (i + 1d) / 60, LastPassDeadline(module)!.Value, 12);
            var data = LastPass(module);
            Assert.Equal(2 - i, data.GetProperty("remaining").GetInt32());
        }
        module.DeadlineClockAndExit();
        Assert.Null(LastPassDeadline(module));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("sync")]
    [InlineData("refusal")]
    [InlineData("late_refusal")]
    [InlineData("no_clock")]
    [InlineData("reset")]
    [InlineData("capture_disabled")]
    public void Actual_render_pass_has_no_deadline_without_a_mapped_owned_simulation(string condition)
    {
        var module = new VrrModule();
        module.CaptureLoops(condition != "capture_disabled");
        module.DeadlineImage(10);
        module.DeadlineClockAndExit();
        module.DeadlineImage(10.05);
        if (condition == "movie") module.NativeMovie = true;
        if (condition == "sync") module.NativeSync = true;
        if (condition == "refusal") module.RefusePresentation("fixture");
        if (condition == "late_refusal") module.DiagnosticCleanup = () => module.RefusePresentation("late");
        if (condition == "reset") module.DeadlineReset();
        module.DeadlineClockAndExit(condition != "no_clock");
        Assert.Null(LastPassDeadline(module));
    }

    [Fact]
    public void Foreign_exit_cannot_report_the_owners_model_deadline()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.DeadlineImage(10);
        module.DeadlineClockAndExit();
        module.DeadlineImage(10.05);
        module.DeadlineClockWithoutExit();
        Exception? failure = null;
        var thread = new Thread(() => { try { module.DeadlineExitOnly(); } catch (Exception e) { failure = e; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.Null(LastPassDeadline(module));
    }

    private static JsonElement LastPass(VrrModule module)
        => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data);

    private static double? LastPassDeadline(VrrModule module)
    {
        var data = LastPass(module);
        Assert.True(data.TryGetProperty("tick_deadline_seconds", out var value), "render_pass must expose its nullable model deadline");
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
    }

    private static bool Query(FixedSimulationClock clock, int index, out double deadline)
        => clock.TryGetStepDeadline(index, out deadline);

    private static double ReadDeadline(FixedSimulationClock clock, int index)
    {
        Assert.True(Query(clock, index, out double deadline));
        return deadline;
    }

    private static void AssertDeadline(FixedSimulationClock clock, int index, double expected)
        => Assert.Equal(expected, ReadDeadline(clock, index), 12);

    private static void AssertUnavailable(FixedSimulationClock clock, int index)
    {
        Assert.False(Query(clock, index, out double deadline));
        Assert.True(double.IsNaN(deadline));
    }
}

public unsafe sealed partial class VrrModule
{
    internal void DeadlineImage(double now)
    {
        PreparePass();
        _render_now = now;
        _render_plan_pending = true;
        _render_input_delta = 1f / 60;
    }

    internal void DeadlineClockAndExit(bool runClock = true)
    {
        _render_inside_loop = true;
        _render_clock_seen = false;
        if (runClock) render_scheduler_clock(1f / 60);
        render_scheduler_exit_loop();
    }

    internal void DeadlineClockWithoutExit() => render_scheduler_clock(1f / 60);
    internal void DeadlineExitOnly() => render_scheduler_exit_loop();
    internal void DeadlineReset() => _simulationClock.Reset();
}
