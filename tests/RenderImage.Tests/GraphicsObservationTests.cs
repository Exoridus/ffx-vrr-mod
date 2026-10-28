using System.Text.Json;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed class GraphicsObservationTests
{
    [Fact]
    public void DiagnosticModeObservesFourBoundariesWithoutImageAdmission()
    {
        using var f = Observer();
        Assert.True(f.Module.InitImages());
        Assert.Equal(new nint[] { 0x242bb0, 0x2419d0, 0x2403e0, 0x2fa70 }.Order(), BridgeNative.Callbacks.Keys.Order());
        Assert.Equal(0u, f.Initialize());
        BridgeNative.OnOrdinary = () => 0xdeadbeef;
        Assert.Equal(0xdeadbeefu, f.Ordinary());
        Assert.Equal(0u, f.FinalizeGraphics());
        f.ResetGraphics();
        Assert.Equal(new[] { "initialize_enter", "initialize_exit", "ordinary_exit", "finalize_enter", "finalize_exit", "reset_enter", "reset_exit" },
            f.Module.GraphicsEvents.Select(e => e.GetProperty("stage").GetString()));
        Assert.Equal(0xdeadbeefu, f.Module.GraphicsEvents[2].GetProperty("native_result").GetUInt32());
        Assert.All(f.Module.GraphicsEvents, e => Assert.False(e.GetProperty("ready").GetBoolean()));
        Assert.False(f.Module.AdmitReady());
        Assert.False(f.Module.MarkAdmitted());
        Assert.Null(f.Module.Failure);
        Assert.Equal(1, BridgeNative.InitializeCalls);
        Assert.Equal(1, BridgeNative.OrdinaryCalls);
        Assert.Equal(1, BridgeNative.FinalizeCalls);
        Assert.Equal(1, BridgeNative.ResetCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BothProbeAndActiveJsonCaptureAreRequired(bool probe, bool capturing)
    {
        using var f = Observer(capturing, probe);
        Assert.True(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
        Assert.Empty(f.Module.GraphicsEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagnosticInstallationPreflightsBeforeAnyHook(bool invalidImage)
    {
        using var f = Observer();
        if (invalidImage) NativeBuild.Supported = false;
        else f.Bytes(0x2419d0, [0xcc]);
        Assert.True(f.Module.InitImages());
        Assert.Empty(BridgeNative.Callbacks);
    }

    [Theory]
    [InlineData("initialize")]
    [InlineData("finalize")]
    [InlineData("reset")]
    [InlineData("ordinary")]
    public void ExceptionalNativeReturnsRemainExceptionalAndUnqualified(string role)
    {
        using var f = Observer();
        Assert.True(f.Module.InitImages());
        var expected = new InvalidOperationException("native failure");
        BridgeNative.OnInitialize = () => throw expected;
        BridgeNative.OnFinalize = () => throw expected;
        BridgeNative.OnReset = () => throw expected;
        BridgeNative.OnOrdinary = () => throw expected;
        Action invoke = role switch { "initialize" => () => f.Initialize(), "finalize" => () => f.FinalizeGraphics(), "reset" => f.ResetGraphics, _ => () => f.Ordinary() };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(invoke));
        JsonElement last = f.Module.GraphicsEvents.Last();
        Assert.Equal(role + "_exit", last.GetProperty("stage").GetString());
        Assert.False(last.GetProperty("normal_return").GetBoolean());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("native_result").ValueKind);
        Assert.False(last.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public void UnknownSchedulerReadRemainsUnknown()
    {
        using var f = Observer();
        Assert.True(f.Module.InitImages());
        MemoryProbe.CopyAllowed = address => address != FhUtil.Base + 0x8dede4;
        f.Ordinary();
        JsonElement sample = Assert.Single(f.Module.GraphicsEvents);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("scheduler").ValueKind);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("group0_free_count").ValueKind);
        Assert.False(sample.GetProperty("snapshot_atomic").GetBoolean());
    }

    [Fact]
    public void StoppedCaptureDoesNotProbeOrEmitOrdinaryEvents()
    {
        using var f = Observer();
        Assert.True(f.Module.InitImages());
        f.Module.GraphicsCapture(false);
        MemoryProbe.CopyAllowed = _ => throw new Exception("unexpected diagnostic read");
        Assert.Equal(0u, f.Ordinary());
        Assert.Empty(f.Module.GraphicsEvents);
    }

    [Fact]
    public void DiagnosticFailureCannotReplaceNativeFailure()
    {
        using var f = Observer();
        Assert.True(f.Module.InitImages());
        var expected = new InvalidOperationException("native failure");
        BridgeNative.OnOrdinary = () => throw expected;
        MemoryProbe.CopyAllowed = _ => throw new Exception("diagnostic failure");
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => f.Ordinary()));
        Assert.Empty(f.Module.GraphicsEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OptionalPartialInstallationCannotBlockRequiredRegistrations(bool throws)
    {
        using var f = Observer();
        if (throws) BridgeNative.ThrowHook = 0x2403e0;
        else BridgeNative.FailHook = 0x2403e0;
        Assert.True(f.Module.InitImages());
        Assert.True(f.Module.QueueRequiredSentinel());
        Assert.Equal(0u, f.Initialize());
        Assert.Equal(0u, f.Ordinary());
        Assert.All(f.Module.GraphicsEvents, sample => Assert.False(sample.GetProperty("ready").GetBoolean()));
        Assert.False(f.Module.MarkAdmitted());
    }

    [Fact]
    public void RequiredIndependentInstallationStillPropagatesSdkFailure()
    {
        using var f = new BridgeFixture();
        BridgeNative.ThrowHook = 0x2403e0;
        Assert.Equal("SDK hook failure", Assert.Throws<InvalidOperationException>(() => f.Module.InitImages()).Message);
    }
    private static BridgeFixture Observer(bool capturing = true, bool probe = true)
    {
        var f = new BridgeFixture();
        f.Module.Disable();
        f.Module.GraphicsCapture(capturing, probe);
        return f;
    }
}
