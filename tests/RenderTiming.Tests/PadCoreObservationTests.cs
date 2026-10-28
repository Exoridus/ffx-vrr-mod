using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadCoreObservationTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    public PadCoreObservationTests()
    {
        Array.Fill(_records, (byte)0xa5);
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
        NativeClock.PollCalls = 0;
        NativeClock.Hooks.Clear();
    }
    public void Dispose()
    {
        FhUtil.PadRecordPointer = 0;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
        NativeClock.Hooks.Clear();
    }

    [Fact]
    public void IndependentObserverSelectionIsRequiredWithoutTelemetryOrProbeOption()
    {
        HookSelection selection = HookRegistrationPlan.ResolveSelection(HookSiteId.DiagnosticBindingsPadPollObservation,
            new VrrConfig { NativePhaseProbe = false }, true, false, false, null);
        Assert.True(selection.Enabled);
        Assert.True(selection.Required);
        Assert.False(selection.Deferred);
    }

    [Fact]
    public void IndependentInitializerRegistersOnceWithoutTelemetry()
    {
        var module = Active();
        module.InitPadObservation();
        module.InitPadObservation();
        Assert.Single(NativeClock.Hooks);
        Assert.Single(module.DiagnosticHookChecks);
    }

    [Fact]
    public void RequiredObserverSdkRefusalRejectsOwnerPlan()
    {
        var module = Active();
        module.RejectPadObservationHook = true;
        module.InitPadObservation();
        Assert.Equal(HookRegistrationState.Refused, module.PadObserverPlanStateForTest);
        Assert.Empty(NativeClock.Hooks);
    }

    [Fact]
    public void FailedObserverBindingCannotAdmitIndependentFrame()
    {
        var module = Active();
        module.PadGetterCodeMatches = false;
        module.InitPadObservation();
        Assert.True(module.InitScheduler());
        module.ArmWithoutPadObserverForTest();
        module.BeginFrame();
        Assert.False(module.PadObserverFrameEnabledForTest);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("enabled")]
    [InlineData("stopped")]
    [InlineData("restarted")]
    [InlineData("dropping")]
    [InlineData("throwing")]
    public void TelemetryCannotChangeCoreCandidates(string telemetry)
    {
        var module = Active();
        if (telemetry != "absent") module.CaptureLoops(true);
        module.InitPadObservation();
        module.SetHookScope(true, true, true, false);
        module.CallPadPortPoll(0, 0, 0x1234);
        long epoch = module.PadObserverEpochForTest;
        if (telemetry == "stopped") module.CaptureLoops(false);
        if (telemetry == "restarted") module.CaptureLoops(true);
        if (telemetry == "dropping") module.LosePadCaptureEvent();
        if (telemetry == "throwing") module.DiagnosticEventAction = kind => { if (kind == "pad_observation") throw new IOException("telemetry failure"); };
        module.CallPadPortPoll(0, 0, 0x5678);
        var candidates = module.ReadPadCandidates(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 1);
        Assert.Equal(new long[] { 1, 2 }, candidates.Observations.Select(v => v.PollSequence));
        Assert.Equal(2L, candidates.Latest[0]!.PollSequence);
        Assert.Null(candidates.Latest[1]);
        Assert.Equal(epoch, module.PadObserverEpochForTest);
        Assert.Equal(2, NativeClock.PollCalls);
        Assert.Equal(2, NativePadObservation.Reads.Count);
        Assert.All(_records, value => Assert.Equal((byte)0xa5, value));
    }

    [Fact]
    public void MappedTickConsumesCandidatesWithoutJsonCapture()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.008, 10.01, 1);
        module.DeadlineImage(10.05);
        module.DeadlineClockAndExit();
        JsonElement correlation = LastPass(module).GetProperty("pad_tick_correlation");
        Assert.Equal(JsonValueKind.Object, correlation.ValueKind);
        Assert.Equal(1L, Assert.Single(correlation.GetProperty("observations").EnumerateArray()).GetProperty("PollSequence").GetInt64());
        Assert.False(correlation.GetProperty("native_input_applied").GetBoolean());
    }

    [Fact]
    public void ZeroStepWithoutJsonRetainsCandidatesForTheNextTick()
    {
        var module = Prime();
        module.SeedPadCandidate(1, 0, 10.001, 10.002, 1);
        module.DeadlineImage(10.005);
        module.DeadlineClockAndExit();
        Assert.Equal(JsonValueKind.Null, LastPass(module).GetProperty("pad_tick_correlation").ValueKind);
        module.DeadlineImage(10.02);
        module.DeadlineClockAndExit();
        JsonElement correlation = LastPass(module).GetProperty("pad_tick_correlation");
        Assert.Equal(JsonValueKind.Object, correlation.ValueKind);
        Assert.Equal(1L, Assert.Single(correlation.GetProperty("observations").EnumerateArray()).GetProperty("PollSequence").GetInt64());
        Assert.False(correlation.GetProperty("native_input_applied").GetBoolean());
    }

    [Theory]
    [InlineData("native_owner")]
    [InlineData("unreadable")]
    [InlineData("copy_exception")]
    [InlineData("poll_exception")]
    [InlineData("unknown_identity")]
    public void NativeBoundariesStillDiscardCandidatesWithoutTelemetry(string condition)
    {
        var module = Active();
        module.InitPadObservation();
        module.SetHookScope(true, true, true, false);
        module.CallPadPortPoll(0, 0, 0);
        var baseline = module.ReadPadCandidates(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        Assert.Equal(1L, baseline.Latest[0]!.PollSequence);
        if (condition == "native_owner") module.NativeMovie = true;
        if (condition == "unreadable") NativePadObservation.FailedPort = 0;
        if (condition == "copy_exception") NativePadObservation.ThrowOnCopy = true;
        var expected = new InvalidOperationException("native poll failure");
        if (condition == "poll_exception") NativeClock.PollAction = () => throw expected;
        if (condition == "poll_exception")
            Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => module.CallPadPortPoll(0, 0, 0)));
        else module.CallPadPortPoll(condition == "unknown_identity" ? 2 : 0, 0, 0);
        var candidates = module.ReadPadCandidates(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency + 1);
        Assert.Empty(candidates.Observations);
        Assert.Null(candidates.Latest[0]);
        Assert.NotNull(module.PadTimelineBoundary);
        Assert.All(_records, value => Assert.Equal((byte)0xa5, value));
    }
    private static JsonElement LastPass(VrrModule module)
        => JsonSerializer.SerializeToElement(module.DiagnosticEvents.Last(e => e.Kind == "render_pass").Data);

    private static VrrModule Active()
    {
        var module = new VrrModule().EnableAdapter();
        module.SetPadProbeOptionForTest(false);
        module.RemovePadCapture();
        return module;
    }
    private static VrrModule Prime()
    {
        var module = Active();
        module.InitPadObservation();
        module.DeadlineImage(10);
        module.CallPadPoll();
        module.DeadlineClockAndExit();
        return module;
    }
}

public unsafe sealed partial class VrrModule
{
    internal void SetPadProbeOptionForTest(bool value) => _config = _config with { NativePhaseProbe = value };
    internal HookRegistrationState? PadObserverPlanStateForTest => _hookRegistration?.State;
    internal bool PadObserverFrameEnabledForTest => _render_frame_enabled;
    internal void ArmWithoutPadObserverForTest() => _render_mode_ready = true;
    internal long PadObserverEpochForTest => _padTimeline.Epoch;
}

internal sealed partial record SchedulerConfig
{
    public bool NativePhaseProbe { get; init; } = true;
}
