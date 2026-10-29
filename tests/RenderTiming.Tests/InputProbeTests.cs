using System.Reflection;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class InputProbeTests : IDisposable
{
    private readonly string? _request = Environment.GetEnvironmentVariable("FFX_VRR_INPUT_PROBE");
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private readonly VrrModule _module = new();

    public InputProbeTests()
    {
        Environment.SetEnvironmentVariable("FFX_VRR_INPUT_PROBE", "1");
        _module.ConfigureInputProbeFixture();
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        for (int i = 0; i < _records.Length; i++) _records[i] = (byte)(i * 7 + 13);
        _records[0x9c] = 1;
        _records[0x19c] = 3;
        NativeClock.PadCalls = NativeClock.SnapshotCalls = 0;
        NativeClock.PadAction = NativeClock.SnapshotAction = NativeClock.Advance = null;
        NativeInputProbeMemory.Enabled = true;
        NativeInputProbeMemory.CopyCalls = 0;
        NativeInputProbeMemory.Script.AsSpan().Clear();
        NativeInputProbeMemory.CopyFailure = NativeInputProbeMemory.CopyThrows = false;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("FFX_VRR_INPUT_PROBE", _request);
        NativeInputProbeMemory.Enabled = false;
        FhUtil.PadRecordPointer = 0;
        NativeClock.PadAction = NativeClock.SnapshotAction = NativeClock.Advance = null;
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }

    [Theory]
    [InlineData("request")]
    [InlineData("phase")]
    [InlineData("capture")]
    [InlineData("rate")]
    [InlineData("independent")]
    public void ProbeRequiresEveryExplicitBoundaryAndDoesNotGrantRenderOwnership(string boundary)
    {
        if (boundary == "request") Environment.SetEnvironmentVariable("FFX_VRR_INPUT_PROBE", null);
        _module.ConfigureInputProbeFixture(boundary);
        Init();
        Assert.False(Enabled());
        Assert.False(_module.ProbeOwnsRenderForTest);
    }

    [Fact]
    public void RequestIsLatchedAndOnlyTwoNativeEntriesAreInstalled()
    {
        Init();
        Assert.True(Enabled());
        Environment.SetEnvironmentVariable("FFX_VRR_INPUT_PROBE", null);
        Assert.True(Enabled());
        NativeClock.HistoryHookRegistrations.Clear();
        Assert.True(_module.InitHistory());
        Assert.Equal(new nint[] { 0x4893e0, 0x42d070 }, NativeClock.HistoryHookRegistrations.Select(x => x.Rva));
        Assert.False(_module.HistoryReady);
        Assert.False(_module.ProbeOwnsRenderForTest);
    }

    [Fact]
    public void CapturedLatchObservesBothNativeHeadsAndScriptSnapshotWithoutWriting()
    {
        Init();
        long root = Begin();
        long clock = EnterClock();
        byte[] before = (byte[])_records.Clone();
        NativeClock.PadAction = () => { _records[0x9c] = 2; _records[0x19c] = 0; _records[0x40 + 4] = 8; };
        Latch();
        ExitClock(clock, true);
        NativeClock.SnapshotAction = () => BitConverter.TryWriteBytes(NativeInputProbeMemory.Script, (ushort)8);
        Snapshot();
        End(root, true);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        before[0x9c] = 2; before[0x19c] = 0; before[0x44] = 8;
        Assert.Equal(before, _records);
        var latch = Event("input_probe_latch");
        Assert.Equal("ordinary_clock", latch.GetProperty("caller_kind").GetString());
        Assert.True(latch.GetProperty("normal_return").GetBoolean());
        Assert.Equal(clock, latch.GetProperty("clock_token").GetInt64());
        Assert.Equal(1, latch.GetProperty("before").GetProperty("ports")[0].GetProperty("head").GetInt32());
        Assert.Equal(2, latch.GetProperty("after").GetProperty("ports")[0].GetProperty("head").GetInt32());
        var snapshot = Event("input_probe_snapshot");
        Assert.Equal(0, snapshot.GetProperty("before").GetProperty("script_word").GetInt32());
        Assert.Equal(8, snapshot.GetProperty("after").GetProperty("script_word").GetInt32());
        Assert.False(_module.ProbeOwnsRenderForTest);
        Assert.True(Event("input_probe_root").GetProperty("normal_return").GetBoolean());
    }

    [Fact]
    public void MenuPublicationIsObservedWithoutInventingAClockOrChangingNativeInput()
    {
        Init();
        long root = Begin();
        FhUtil.MenuPause = 1;
        byte[] before = (byte[])_records.Clone();
        try { Latch(0x820ba8); End(root, true); }
        finally { FhUtil.MenuPause = 0; }
        var data = Event("input_probe_latch");
        Assert.Equal("menu_pause", data.GetProperty("caller_kind").GetString());
        Assert.Equal(0, data.GetProperty("clock_token").GetInt64());
        Assert.Equal(before, _records);
        Assert.Equal(1, NativeClock.PadCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeExceptionsKeepIdentityAndAreNotRetried(bool snapshot)
    {
        Init(); long root = Begin();
        var error = new InvalidOperationException("native probe fixture");
        if (snapshot) NativeClock.SnapshotAction = () => throw error;
        else NativeClock.PadAction = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => { if (snapshot) Snapshot(); else Latch(); }));
        End(root, false);
        Assert.Equal(1, snapshot ? NativeClock.SnapshotCalls : NativeClock.PadCalls);
        Assert.False(Event(snapshot ? "input_probe_snapshot" : "input_probe_latch").GetProperty("normal_return").GetBoolean());
        Assert.False(Event("input_probe_root").GetProperty("normal_return").GetBoolean());
    }

    [Fact]
    public void SamplingAndTelemetryFailureCannotSkipNativeForwarding()
    {
        Init(); long root = Begin();
        NativeInputProbeMemory.CopyThrows = true;
        _module.DiagnosticEventAction = _ => throw new InvalidOperationException("telemetry");
        Latch(); Snapshot(); End(root, true);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, NativeClock.SnapshotCalls);
    }

    [Fact]
    public void PreArmProbeForwardsWithoutReadingMemory()
    {
        Init(); _module.StopSyntheticRuntimeForTest();
        NativeInputProbeMemory.CopyThrows = true;
        Latch(); Snapshot();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, NativeClock.SnapshotCalls);
        Assert.DoesNotContain(_module.DiagnosticEvents, x => x.Kind is "input_probe_latch" or "input_probe_snapshot");
    }

    [Fact]
    public void ProductionDiagnosticLoopBalancesTheNativeClockLatchAndSnapshot()
    {
        Init();
        NativeClock.Advance = () => Latch();
        NativeClock.LoopAction = _ => { _module.SnapshotClock(1f / 60); Snapshot(); };
        try { _module.CallDiagnosticLoop(1f / 60); }
        finally { NativeClock.LoopAction = null; }
        var root = Event("input_probe_root");
        Assert.True(root.GetProperty("normal_return").GetBoolean());
        Assert.Equal(1, root.GetProperty("clock_calls").GetInt32());
        Assert.Equal(1, root.GetProperty("clock_returns").GetInt32());
        Assert.Equal(1, root.GetProperty("latch_calls").GetInt32());
        Assert.Equal(1, root.GetProperty("snapshot_calls").GetInt32());
        Assert.True(Event("input_probe_latch").GetProperty("owned").GetBoolean());
        Assert.False(_module.ProbeOwnsRenderForTest);
    }

    [Fact]
    public void CaptureLossLeavesTheProbeLatchedButStopsMemoryReads()
    {
        Init(); _module.CaptureLoops(false);
        Latch(); Snapshot();
        Assert.True(Enabled());
        Assert.Equal(0, NativeInputProbeMemory.CopyCalls);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, NativeClock.SnapshotCalls);
    }
    [Fact]
    public void ForeignPublicationIsRecordedWithoutBorrowingTheOwnersClock()
    {
        Init(); long root = Begin(); long clock = EnterClock();
        Exception? error = null;
        var thread = new Thread(() => { try { Latch(); } catch (Exception failure) { error = failure; } });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.Null(error);
        ExitClock(clock, true); End(root, true);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(Event("input_probe_latch").GetProperty("owned").GetBoolean());
        Assert.Equal(0, Event("input_probe_latch").GetProperty("clock_token").GetInt64());
        Assert.Equal(0, Event("input_probe_root").GetProperty("latch_calls").GetInt32());
    }

    [Fact]
    public void NestedLoopCannotReplaceTheTrackedRoot()
    {
        Init(); long root = Begin();
        Assert.Equal(0, Begin());
        Latch(); End(0, true); End(root, true);
        Assert.Equal(root, Event("input_probe_root").GetProperty("root").GetInt64());
        Assert.True(Event("input_probe_root").GetProperty("invalid").GetBoolean());
        Assert.Equal(1, NativeClock.PadCalls);
    }

    [Fact]
    public void UnreadableObservationsRemainExplicitAndNeverPreventNativeWork()
    {
        Init(); long root = Begin();
        NativeInputProbeMemory.CopyFailure = true;
        Latch(); End(root, true);
        var before = Event("input_probe_latch").GetProperty("before");
        Assert.All(before.GetProperty("ports").EnumerateArray(), port => Assert.False(port.GetProperty("valid").GetBoolean()));
        Assert.False(before.GetProperty("script_valid").GetBoolean());
        Assert.Equal(1, NativeClock.PadCalls);
    }
    private void Init() => Method("init_input_probe").CreateDelegate<Action>(_module)();
    private bool Enabled() => (bool)(typeof(VrrModule).GetProperty("InputProbeEnabled", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_module) ?? false);
    private long Begin() => _module.ProbeRootForTest(Method("input_probe_enter_loop"));
    private void End(long scope, bool normal) => Method("input_probe_exit_loop").CreateDelegate<Action<long, bool>>(_module)(scope, normal);
    private long EnterClock() => Method("input_probe_enter_clock").CreateDelegate<Func<long>>(_module)();
    private void ExitClock(long clock, bool normal) => Method("input_probe_exit_clock").CreateDelegate<Action<long, bool>>(_module)(clock, normal);
    private void Latch(nuint address = 0x822224)
    {
        uint slot = checked((uint)address);
        _module.ProbeLatchForTest(Method("input_probe_latch_invoke"), address, (nuint)(&slot));
    }
    private void Snapshot() => _module.ProbeSnapshotForTest(Method("input_probe_snapshot_invoke"));
    private static MethodInfo Method(string name)
    {
        var method = typeof(VrrModule).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method;
    }
    private JsonElement Event(string kind) => JsonSerializer.SerializeToElement(_module.DiagnosticEvents.Last(x => x.Kind == kind).Data);
}

public unsafe sealed partial class VrrModule
{
    internal void ConfigureInputProbeFixture(string? missing = null)
    {
        ChosenRate = missing == "rate" ? 30 : 60;
        IndependentPresentationEnabled = missing == "independent";
        _config = _config with { NativePhaseProbe = missing != "phase" };
        CaptureLoops(missing != "capture");
        _render_scheduler_active = _render_mode_ready = _render_frame_enabled = _render_inside_loop = false;
        _pad_latch_installed = _pad_history_readers_ready = _padSnapshotInstalled = false;
        RenderPassContext.IsActive = RenderPassContext.IsPresentation = false;
    }
    internal bool ProbeOwnsRenderForTest => OwnsRenderPass || RenderPassContext.IsActive || RenderPassContext.IsPresentation;
    internal long ProbeRootForTest(MethodInfo method)
    {
        var scope = pad_snapshot_enter_loop();
        return (long)method.Invoke(this, new object[] { scope })!;
    }
    internal void ProbeLatchForTest(MethodInfo method, nuint address, nuint slot)
        => method.CreateDelegate<Action<nuint, nuint, d_render_void>>(this)(address, slot, NativeClock.Pad);
    internal void ProbeSnapshotForTest(MethodInfo method)
        => method.CreateDelegate<Action<nuint, nuint, d_render_void>>(this)(0x820df5, 0, NativeClock.Snapshot);
}

internal static unsafe class NativeInputProbeMemory
{
    internal static bool Enabled, CopyFailure, CopyThrows;
    internal static int CopyCalls;
    internal static readonly byte[] Script = GC.AllocateArray<byte>(2, pinned: true);
    internal static nint ScriptPointer { get { fixed (byte* p = Script) return (nint)p; } }
    internal static bool TryCopy(nint address, Span<byte> target, out bool result)
    {
        result = false;
        if (!Enabled || (address != ScriptPointer && (address < FhUtil.PadRecordPointer || address + target.Length > FhUtil.PadRecordPointer + 0x200))) return false;
        CopyCalls++;
        if (CopyThrows) throw new InvalidOperationException("input probe copy fixture");
        if (!CopyFailure) { new ReadOnlySpan<byte>((void*)address, target.Length).CopyTo(target); result = true; }
        return true;
    }
}
