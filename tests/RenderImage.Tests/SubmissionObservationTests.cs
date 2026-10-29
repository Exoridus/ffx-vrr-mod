using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public sealed class SubmissionObservationTests
{
    [Fact]
    public void ExplicitProbeInstallsGuardedGroupWithoutAdmission()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        Assert.Equal(8, BridgeNative.Callbacks.Count);
        Assert.False(f.Module.AdmitReady());
        Assert.False(f.Module.MarkAdmitted());
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData(null, true, true, 60)]
    [InlineData("0", true, true, 60)]
    [InlineData("1", false, true, 60)]
    [InlineData("1", true, false, 60)]
    [InlineData("1", true, true, 30)]
    public void GateDoesNotBroadenDefaultObservation(string? value, bool probe, bool capture, int hz)
    {
        using var env = new ProbeEnvironment(value);
        using var f = Observer(capture, probe);
        f.Module.SubmissionChosenHz = hz;
        f.Init(false);
        Assert.DoesNotContain((nint)0x227940, BridgeNative.Callbacks.Keys);
        Assert.DoesNotContain((nint)0x193760, BridgeNative.Callbacks.Keys);
    }

    [Fact]
    public void NativeScopeForwardsOnceAndObservesPairingWithoutScheduling()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            f.Module.AnimationEnd(f.Application);
            f.Ordinary();
            return f.Flip();
        };
        f.Frame();
        Assert.Equal(0xaabbcc01u, f.Action());
        Assert.Equal(1, BridgeNative.Frames);
        Assert.Equal(1, BridgeNative.Flips);
        Assert.Equal(1, BridgeNative.Slots);
        Assert.Equal(1, BridgeNative.Actions);
        var action = f.Module.SubmissionEvents.Last(e => e.GetProperty("stage").GetString() == "action_exit");
        Assert.True(action.GetProperty("matched").GetBoolean());
        Assert.True(action.GetProperty("ordinary_complete").GetBoolean());
        Assert.False(action.GetProperty("independent_admitted").GetBoolean());
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void NativeExceptionRetainsIdentityAndEndsPassiveScope()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        var expected = new InvalidOperationException("native frame");
        BridgeNative.OnFrame = _ => throw expected;
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => f.Frame()));
        var last = f.Module.SubmissionEvents.Last();
        Assert.Equal("frame_exit", last.GetProperty("stage").GetString());
        Assert.False(last.GetProperty("normal_return").GetBoolean());
        Assert.Equal(0, last.GetProperty("active_frames").GetInt32());
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void UnmatchedActionIsDiagnosticOnly()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        Assert.Equal(0xaabbcc01u, f.Action());
        var last = f.Module.SubmissionEvents.Last();
        Assert.False(last.GetProperty("matched").GetBoolean());
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void FullSubmissionBindingsAreCheckedBeforeInstallingProbeHooks()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Bytes(0x193760, [0xcc]);
        Assert.True(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
    }

    [Fact]
    public void NestedFramesBalanceWithoutRefusingNativeOutput()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        int depth = 0;
        BridgeNative.OnFrame = _ => ++depth == 1 ? f.Frame() : 0x12345678;
        Assert.Equal(0x12345678u, f.Frame());
        Assert.Equal(2, BridgeNative.Frames);
        var exits = f.Module.SubmissionEvents.Where(e => e.GetProperty("stage").GetString() == "frame_exit").ToArray();
        Assert.Equal(2, exits.Length);
        Assert.Equal(0, exits.Last().GetProperty("active_frames").GetInt32());
        Assert.Contains(exits, e => !e.GetProperty("matched").GetBoolean());
        Assert.Null(f.Module.Failure);
    }

    [Theory]
    [InlineData("flip")]
    [InlineData("slot")]
    [InlineData("action")]
    public void NativeFailureAtEveryStageForwardsOnceAndRetainsException(string stage)
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        var error = new InvalidOperationException(stage);
        if (stage == "flip") BridgeNative.OnFlip = _ => throw error;
        if (stage == "slot") BridgeNative.OnSlot = _ => throw error;
        if (stage == "action") BridgeNative.OnAction = _ => throw error;
        Action invoke = stage switch { "flip" => () => f.Flip(), "slot" => () => f.Slot(), _ => () => f.Action() };
        Assert.Same(error, Assert.Throws<InvalidOperationException>(invoke));
        Assert.Equal(1, stage switch { "flip" => BridgeNative.Flips, "slot" => BridgeNative.Slots, _ => BridgeNative.Actions });
        Assert.False(f.Module.SubmissionEvents.Last().GetProperty("normal_return").GetBoolean());
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void FailedFlipCannotLeaveAnApparentlyMatchedAction()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        BridgeNative.OnFrame = _ => f.Flip();
        BridgeNative.OnFlip = _ => { f.Slot(); throw new InvalidOperationException("flip failed after slot"); };
        Assert.Throws<InvalidOperationException>(() => f.Frame());
        f.Action();
        Assert.False(f.Module.SubmissionEvents.Last().GetProperty("matched").GetBoolean());
        Assert.Null(f.Module.Failure);
    }

    [Fact]
    public void NestedOrdinaryReturnsCannotRefusePassiveOutput()
    {
        using var env = new ProbeEnvironment("1");
        using var f = Observer();
        f.Init();
        int depth = 0;
        BridgeNative.OnFrame = _ =>
        {
            f.Module.Animate(f.Application);
            f.Module.AnimationEnd(f.Application);
            return f.Ordinary();
        };
        BridgeNative.OnOrdinary = () => ++depth == 1 ? f.Ordinary() : 0;
        f.Frame();
        Assert.Equal(2, BridgeNative.OrdinaryCalls);
        Assert.Null(f.Module.Failure);
    }

    private static BridgeFixture Observer(bool capture = true, bool probe = true)
    {
        var f = new BridgeFixture();
        f.Module.Disable();
        f.Module.GraphicsCapture(capture, probe);
        return f;
    }

    private sealed class ProbeEnvironment(string? value) : IDisposable
    {
        private readonly string? _previous = Set(value);
        private static string? Set(string? value)
        {
            string? previous = Environment.GetEnvironmentVariable("FFX_VRR_SUBMISSION_PROBE");
            Environment.SetEnvironmentVariable("FFX_VRR_SUBMISSION_PROBE", value);
            return previous;
        }
        public void Dispose() => Environment.SetEnvironmentVariable("FFX_VRR_SUBMISSION_PROBE", _previous);
    }
}

public unsafe sealed partial class VrrModule
{
    internal readonly List<JsonElement> SubmissionEvents = [];
    internal double SubmissionChosenHz = 60;
    private double? ChosenFramerate => SubmissionChosenHz;
}
