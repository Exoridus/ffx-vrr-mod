using Fahrenheit.Mods.Vrr;
using Xunit;

public sealed class FixedSimulationClockTests
{
    public FixedSimulationClockTests() => RenderPassContext.IsActive = false;

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    public void Simulation_count_depends_on_time_instead_of_present_count(int presentationHz)
    {
        var clock = new FixedSimulationClock();
        int ticks = 0;
        for (int frame = 0; frame <= presentationHz; frame++)
            ticks += clock.Plan((double)frame / presentationHz).Steps;
        Assert.Equal(61, ticks);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(30, 144)]
    [InlineData(60, 30)]
    [InlineData(60, 144)]
    [InlineData(120, 30)]
    [InlineData(120, 144)]
    public void Selected_simulation_rate_is_independent_of_render_cap(int simulationHz, int renderHz)
    {
        var clock = new FixedSimulationClock(simulationHz);
        int ticks = 0;
        for (int image = 0; image <= renderHz; image++)
            ticks += clock.Plan((double)image / renderHz).Steps;
        Assert.Equal(simulationHz + 1, ticks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    [InlineData(90)]
    [InlineData(144)]
    public void Rejects_unsupported_simulation_rates(int rate)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new FixedSimulationClock(rate));
    [Fact]
    public void Variable_presentation_preserves_sixty_steps_per_second()
    {
        var clock = new FixedSimulationClock();
        int ticks = clock.Plan(0).Steps;
        double now = 0;
        for (int pair = 0; pair < 30; pair++)
        {
            ticks += clock.Plan(now += 1d / 120).Steps;
            ticks += clock.Plan(now += 1d / 40).Steps;
        }
        Assert.Equal(61, ticks);
    }

    [Fact]
    public void Extra_images_do_not_consume_simulation_steps()
    {
        var clock = new FixedSimulationClock();
        Assert.Equal(1, clock.Plan(0).Steps);
        var extra = clock.Plan(1d / 120);
        Assert.Equal(0, extra.Steps);
        Assert.Equal(0.5, extra.Alpha, 6);
        Assert.Equal(1, clock.Plan(1d / 60).Steps);
    }

    [Fact]
    public void Hitch_within_the_engine_clamp_is_caught_up_in_full()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        var plan = clock.Plan(0.2);
        Assert.Equal(12, plan.Steps);
        Assert.Equal(0, plan.DroppedSteps);
        Assert.False(plan.Reset);
        Assert.Equal(0, clock.Plan(0.2).Steps);
    }

    [Fact]
    public void Hitch_beyond_the_engine_clamp_covers_the_clamp_and_resets_history()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        var plan = clock.Plan(1.7);
        Assert.Equal(18, plan.Steps);
        Assert.Equal(84, plan.DroppedSteps);
        Assert.True(plan.Reset);
        Assert.Equal(1, clock.Plan(1.7 + 1d / 60).Steps);
    }

    [Fact]
    public void Speed_boost_changes_step_count_without_changing_step_duration()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        Assert.Equal(2, clock.Plan(1d / 120, 4).Steps);
        Assert.Equal(2, clock.Plan(2d / 120, 4).Steps);
        Assert.Equal(0, clock.Plan(3d / 120, 1).Steps);
    }

    [Theory]
    [InlineData(30, 0, 0, 0)]
    [InlineData(60, 0.5, 9, 42)]
    [InlineData(60, 8, 144, 672)]
    [InlineData(120, 8, 288, 1344)]
    [InlineData(60, -1, 0, 0)]
    [InlineData(60, 100, 144, 672)]
    [InlineData(60, double.NaN, 18, 84)]
    [InlineData(60, double.PositiveInfinity, 18, 84)]
    public void Discarded_steps_use_the_same_normalized_speed_as_admitted_time(
        int simulationHz, double speed, int steps, int droppedSteps)
    {
        var clock = new FixedSimulationClock(simulationHz);
        clock.Plan(0);
        var plan = clock.Plan(1.7, speed);
        Assert.Equal(steps, plan.Steps);
        Assert.Equal(droppedSteps, plan.DroppedSteps);
        Assert.True(plan.Reset);
        Assert.Equal(0, plan.Alpha);
    }

    [Fact]
    public void Extreme_finite_gap_saturates_discarded_steps_without_wrapping()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        var plan = clock.Plan(double.MaxValue, 8);
        Assert.Equal(144, plan.Steps);
        Assert.Equal(int.MaxValue, plan.DroppedSteps);
        Assert.Equal(0, plan.Alpha);
        Assert.True(plan.Reset);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void Tick_boundary_rounding_preserves_time_across_image_partitions(int simulationHz)
    {
        var fine = new FixedSimulationClock(simulationHz);
        var grouped = new FixedSimulationClock(simulationHz);
        fine.Plan(0);
        grouped.Plan(0);
        double interval = 1d / simulationHz - 5e-10;
        int fineSteps = 0, groupedSteps = 0;
        SimulationFramePlan finePlan = default, groupedPlan = default;
        for (int image = 1; image <= 200; image++)
        {
            finePlan = fine.Plan(image * interval);
            fineSteps += finePlan.Steps;
            if (image % 5 == 0)
            {
                groupedPlan = grouped.Plan(image * interval);
                groupedSteps += groupedPlan.Steps;
            }
        }
        Assert.Equal(199, fineSteps);
        Assert.Equal(groupedSteps, fineSteps);
        Assert.Equal(groupedPlan.Alpha, finePlan.Alpha, 5);
    }

    [Theory]
    [InlineData(30, 1, 8)]
    [InlineData(60, 1, 16)]
    [InlineData(120, 1, 32)]
    [InlineData(60, 8, 16)]
    public void Borrowed_tick_tolerance_never_produces_negative_steps_on_extra_images(
        int simulationHz, double speed, int steps)
    {
        var clock = new FixedSimulationClock(simulationHz);
        clock.Plan(0);
        double timestamp = (steps / (double)simulationHz - 1e-9) / speed;
        Assert.Equal(steps, clock.Plan(timestamp, speed).Steps);
        var extra = clock.Plan(timestamp, speed);
        Assert.Equal(0, extra.Steps);
        Assert.Equal(0, extra.Alpha);
        Assert.Equal(1, clock.Plan((steps + 1d) / simulationHz / speed, speed).Steps);
    }

    [Theory]
    [InlineData(30, 0.5, 3, 0.75)]
    [InlineData(60, 0.5, 7, 0.5)]
    [InlineData(120, 0.5, 15, 0)]
    [InlineData(30, 8, 60, 0)]
    [InlineData(60, 8, 120, 0)]
    [InlineData(120, 8, 240, 0)]
    public void Irregular_images_preserve_scaled_steps_and_fractional_remainder(
        int simulationHz, double speed, int steps, double alpha)
    {
        var clock = new FixedSimulationClock(simulationHz);
        clock.Plan(0);
        int total = 0;
        SimulationFramePlan plan = default;
        foreach (double timestamp in new[] { 0.003, 0.014, 0.027, 0.068, 0.168, 0.25 })
        {
            plan = clock.Plan(timestamp, speed);
            total += plan.Steps;
            Assert.Equal(0, plan.DroppedSteps);
            Assert.False(plan.Reset);
            Assert.InRange(plan.Alpha, 0, 1);
        }
        Assert.Equal(steps, total);
        Assert.Equal(alpha, plan.Alpha, 5);

        var single = new FixedSimulationClock(simulationHz);
        single.Plan(0);
        var singlePlan = single.Plan(0.25, speed);
        Assert.Equal(total, singlePlan.Steps);
        Assert.Equal(plan.Alpha, singlePlan.Alpha, 5);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(double.MinValue)]
    public void Invalid_elapsed_time_resets_fractional_history(double timestamp)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        Assert.Equal(0.5, clock.Plan(1d / 120).Alpha, 6);
        var plan = clock.Plan(timestamp, 8);
        Assert.Equal(1, plan.Steps);
        Assert.Equal(0, plan.DroppedSteps);
        Assert.Equal(0, plan.Alpha);
        Assert.True(plan.Reset);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_finite_timestamp_after_a_nonfinite_sample_restarts_the_clock(double timestamp)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(timestamp);
        var recovered = clock.Plan(0);
        Assert.Equal(1, recovered.Steps);
        Assert.True(recovered.Reset);
        Assert.Equal(1, clock.Plan(1d / 60).Steps);
    }

    [Fact]
    public void Catch_up_passes_consume_water_time_only_once_per_image()
    {
        RenderPassContext.BeginImage(0.05);
        double frames = 0;
        for (int pass = 0; pass < 3; pass++) frames += 60 * RenderPassContext.TakeWaterSeconds();
        Assert.Equal(3, frames, 6);
        RenderPassContext.BeginImage(1d / 120);
        Assert.Equal(0.5, 60 * RenderPassContext.TakeWaterSeconds(), 6);
    }

    [Fact]
    public void Native_worker_jobs_observe_the_owned_render_phase()
    {
        bool observed = false;
        RenderPassContext.IsPresentation = true;
        try
        {
            var worker = new Thread(() => observed = RenderPassContext.IsPresentation);
            worker.Start();
            worker.Join();
            Assert.True(observed);
        }
        finally { RenderPassContext.IsPresentation = false; }
    }

    [Theory]
    [InlineData(0u, false, 0u)]
    [InlineData(1u, false, 1u)]
    [InlineData(3u, false, 1u)]
    [InlineData(1u, true, 0u)]
    [InlineData(3u, true, 0u)]
    public void Scheduled_pass_exec_is_bounded_to_one_simulation(uint native, bool presentation, uint expected)
        => Assert.Equal(expected, FixedSimulationClock.ResolveExec(native, presentation));

    [Theory]
    [InlineData(10, 18)]
    [InlineData(-1, 1)]
    [InlineData(double.NaN, 1)]
    public void Suspension_or_invalid_time_discards_stale_interpolation(double now, int steps)
    {
        var clock = new FixedSimulationClock();
        clock.Plan(0);
        var plan = clock.Plan(now);
        Assert.True(plan.Reset);
        Assert.Equal(steps, plan.Steps);
        Assert.Equal(0, plan.Alpha);
    }
}
