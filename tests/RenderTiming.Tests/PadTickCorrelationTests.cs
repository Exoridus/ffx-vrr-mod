using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadTickCorrelationTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    public PadTickCorrelationTests()
    {
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
    }
    public void Dispose()
    {
        FhUtil.PadRecordPointer = 0;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
    }
    [Fact]
    public void Actual_ticks_keep_later_observations_pending_and_preserve_latest_state()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        module.SeedPadCandidate(2, 0, 10.039, 10.04, 0);
        module.DeadlineImage(10.05);
        foreach (long[] expected in new[] { new long[] { 1 }, [], new long[] { 2 } })
        {
            module.DeadlineClockAndExit();
            var data = Correlation(module);
            Assert.Equal("candidate_only", data.GetProperty("status").GetString());
            Assert.False(data.GetProperty("native_input_applied").GetBoolean());
            Assert.Equal(expected, data.GetProperty("observations").EnumerateArray().Select(v => v.GetProperty("PollSequence").GetInt64()));
        }
        Assert.Equal(2, module.LastPadCandidateSequence(0));
        Assert.All(_records, b => Assert.Equal(0, b));
    }
    [Fact]
    public void Presentation_image_retains_pending_observations_until_next_tick()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(10.005);
        module.DeadlineClockAndExit();
        Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("pad_tick_correlation").ValueKind);
        module.DeadlineImage(10.02);
        module.DeadlineClockAndExit();
        Assert.Single(Correlation(module).GetProperty("observations").EnumerateArray());
    }
    [Theory]
    [InlineData("movie")]
    [InlineData("sync")]
    [InlineData("overdrive")]
    [InlineData("menu")]
    [InlineData("main_menu")]
    [InlineData("reservation")]
    [InlineData("dummy")]
    [InlineData("capture_off")]
    [InlineData("capture_changed")]
    [InlineData("capture_loss")]
    [InlineData("no_clock")]
    [InlineData("refusal")]
    [InlineData("unready")]
    [InlineData("speed_change")]
    [InlineData("reset")]
    [InlineData("long_gap")]
    public void Discontinuity_clears_candidates_instead_of_assigning_stale_input(string condition)
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(condition == "long_gap" ? 11 : 10.05);
        if (condition == "movie") module.NativeMovie = true;
        if (condition == "sync") module.NativeSync = true;
        if (condition == "overdrive") FhUtil.Overdrive = 1;
        if (condition == "dummy") FhUtil.Dummy = 1;
        if (condition == "menu") FhUtil.MenuPause = 1;
        if (condition == "main_menu") FhUtil.MainMenu = 1;
        if (condition == "reservation") FhUtil.MenuReservation = 0;
        if (condition == "capture_off") module.CaptureLoops(false);
        if (condition == "capture_changed") module.CaptureLoops(true);
        if (condition == "capture_loss") module.LosePadCaptureEvent();
        if (condition == "refusal") module.DiagnosticCleanup = () => module.RefusePresentation("late");
        if (condition == "unready") module.InputReadiness(false, false);
        if (condition == "speed_change") module.ChangePadPlanSpeed();
        if (condition == "reset") module.DeadlineReset();
        try
        {
            module.DeadlineClockAndExit(condition != "no_clock");
            Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("pad_tick_correlation").ValueKind);
            Assert.Null(module.LastPadCandidateSequence(0));
            Assert.NotNull(module.PadTimelineBoundary);
        }
        finally { FhUtil.MenuReservation = -1; FhUtil.MainMenu = FhUtil.Overdrive = FhUtil.MenuPause = 0; FhUtil.Dummy = 0; }
    }
    [Fact]
    public void Actual_poll_retains_both_bit_exact_port_samples_without_writing_native_memory()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.SetHookScope(true, true, true, false);
        Array.Fill(_records, (byte)0xa5);
        byte[] before = (byte[])_records.Clone();
        module.CallPadPoll();
        var result = module.ReadPadCandidates(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 1);
        Assert.Equal(2, result.Observations.Length);
        Assert.Equal(new[] { 0, 1 }, result.Observations.Select(x => x.Port));
        Assert.All(result.Observations, x => Assert.Equal(Enumerable.Repeat(0xa5a5a5a5u, 8), x.RawWords));
        Assert.Equal(before, _records);
    }
    [Fact]
    public void Foreign_poll_remains_logged_but_invalidates_owned_candidates()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        Exception? failure = null;
        var thread = new Thread(() => { try { module.CallPadPoll(); } catch (Exception e) { failure = e; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.Equal(2, module.DiagnosticEvents.Count(e => e.Kind == "pad_observation"));
        Assert.Null(module.LastPadCandidateSequence(0));
    }
    [Theory]
    [InlineData("native")]
    [InlineData("copy")]
    [InlineData("unreadable")]
    public void Poll_failure_clears_pending_candidates(string condition)
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        if (condition == "native") NativeClock.PollAction = () => throw new InvalidOperationException("native");
        if (condition == "copy") NativePadObservation.ThrowOnCopy = true;
        if (condition == "unreadable") NativePadObservation.FailedPort = 0;
        if (condition == "native") Assert.Throws<InvalidOperationException>(() => module.CallPadPoll());
        else module.CallPadPoll();
        Assert.Empty(module.ReadPadCandidates(20).Observations);
        Assert.NotNull(module.PadTimelineBoundary);
    }
    [Fact]
    public void Mapping_signal_covers_zero_step_images_and_rejects_changed_speed()
    {
        var clock = new FixedSimulationClock();
        clock.Plan(10);
        Assert.False(clock.HasMappedInterval);
        Assert.Equal(0, clock.Plan(10.001).Steps);
        Assert.True(clock.HasMappedInterval);
        Assert.Equal(0, clock.Plan(10.002, 2).Steps);
        Assert.False(clock.HasMappedInterval);
        clock.Plan(10.003, 2);
        Assert.True(clock.HasMappedInterval);
        clock.Reset();
        Assert.False(clock.HasMappedInterval);
    }
    [Fact]
    public void Native_loop_exception_after_clock_revokes_all_candidate_input()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(10.05);
        var error = new InvalidOperationException("native loop");
        NativeClock.LoopAction = _ => { module.DeadlineClockWithoutExit(); throw error; };
        try
        {
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(1f / 60)));
            Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("pad_tick_correlation").ValueKind);
            Assert.Null(module.LastPadCandidateSequence(0));
            Assert.Equal("native_loop_exception", module.PadTimelineBoundary);
        }
        finally { NativeClock.LoopAction = null; }
    }
    [Fact]
    public void Linked_scheduler_preserves_exact_candidates_and_deadlines_across_image_partitions()
    {
        var grouped = RunPartitionedImages([10.25]);
        var split = RunPartitionedImages([10.001, 10.017, 10.25]);
        Assert.Equal(grouped, split);
        Assert.Equal("[]", grouped[5].Observations);
        Assert.Contains("\"PollSequence\":1", grouped[6].Observations);
        Assert.Contains("\"PollSequence\":2", grouped[6].Observations);
        Assert.All(_records, b => Assert.Equal(0, b));
    }

    private static List<(double Deadline, string Observations, string Latest)> RunPartitionedImages(double[] images)
    {
        var module = Prime();
        for (int port = 0; port < 2; port++)
        {
            module.SeedPadCandidate(1, port, 10.0999999, 10.1, 1);
            module.SeedPadCandidate(2, port, 10.1, 10.1000001, 0);
        }
        List<(double, string, string)> result = [];
        foreach (double now in images)
        {
            module.DeadlineImage(now);
            JsonElement pass;
            do
            {
                module.DeadlineClockAndExit();
                pass = LastPass(module);
                var deadline = pass.GetProperty("tick_deadline_seconds");
                if (deadline.ValueKind == JsonValueKind.Null)
                {
                    Assert.False(pass.GetProperty("simulation").GetBoolean());
                    Assert.Equal(JsonValueKind.Null, pass.GetProperty("pad_tick_correlation").ValueKind);
                    continue;
                }
                var correlation = Correlation(module);
                Assert.Equal("candidate_only", correlation.GetProperty("status").GetString());
                Assert.False(correlation.GetProperty("native_input_applied").GetBoolean());
                Assert.False(correlation.GetProperty("native_continuity_proven").GetBoolean());
                result.Add((deadline.GetDouble(), correlation.GetProperty("observations").GetRawText(),
                    correlation.GetProperty("latest").GetRawText()));
            } while (pass.GetProperty("remaining").GetInt32() > 0);
        }
        Assert.Equal(15, result.Count);
        return result;
    }
    private static VrrModule Prime()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.DeadlineImage(10);
        module.CallPadPoll();
        module.DeadlineClockAndExit();
        module.DiagnosticEvents.Clear();
        return module;
    }
    private static JsonElement LastPass(VrrModule module) => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data);
    private static JsonElement Correlation(VrrModule module)
    {
        var data = LastPass(module);
        Assert.True(data.TryGetProperty("pad_tick_correlation", out var result), "render_pass must expose candidate-only input correlation");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        return result;
    }
}

public unsafe sealed partial class VrrModule
{
    internal void SeedPadCandidate(long sequence, int port, double start, double end, uint held)
    {
        uint[] words = new uint[8]; words[6] = held;
        Assert.True(_padTimeline.Observe(sequence, port, (long)(start * Stopwatch.Frequency), (long)(end * Stopwatch.Frequency), words));
    }
    internal PadTickCorrelation ReadPadCandidates(double deadline) => _padTimeline.Correlate(deadline);
    internal long? LastPadCandidateSequence(int port) => _padTimeline.Correlate(100).Latest[port]?.PollSequence;
    internal string? PadTimelineBoundary => _padTimeline.Boundary;
    internal void LosePadCaptureEvent() => _diagnostics!.DroppedEvents++;
    internal void ChangePadPlanSpeed() => _render_input_delta = 1f / 30;
}