using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class DiagnosticLoopTimingTests : IDisposable
{
    public void Dispose()
    {
        NativeClock.LoopAction = null;
        VrrModule.ClearDiagnosticLoop();
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_loop_times_only_the_native_call_and_records_return_status(bool throws)
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        module.SetHookScope(true, true, true, false);
        long inside = 0, cleanup = 0;
        module.DiagnosticCleanup = () => cleanup = Stopwatch.GetTimestamp();
        var error = new InvalidOperationException("native fixture");
        NativeClock.LoopAction = delta =>
        {
            Assert.Equal(0.125f, delta);
            inside = Stopwatch.GetTimestamp();
            if (throws) throw error;
        };
        if (throws) Assert.Same(error, Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(0.125f)));
        else module.CallDiagnosticLoop(0.125f);

        var enter = Assert.Single(module.DiagnosticEvents, e => e.Kind == "loop_enter");
        var exit = Assert.Single(module.DiagnosticEvents, e => e.Kind == "loop_exit");
        Assert.Equal(enter.Loop, exit.Loop);
        Assert.True(exit.Loop > 0);
        JsonElement data = JsonSerializer.SerializeToElement(exit.Data);
        Assert.True(data.TryGetProperty("native_start_ticks", out JsonElement start), "loop_exit must report the native-call start");
        long end = data.GetProperty("native_end_ticks").GetInt64();
        Assert.InRange(inside, start.GetInt64(), end);
        Assert.True(end <= cleanup, "Measurement must end before scheduler cleanup");
        Assert.Equal(!throws, data.GetProperty("native_normal_return").GetBoolean());
        foreach (string field in new[] { "source2", "source3", "view_bits", "effective_exec", "backlog" })
            Assert.True(data.TryGetProperty(field, out _), "Existing flat view field must remain: " + field);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }

    [Fact]
    public void Nested_loops_keep_separate_intervals_and_restore_the_outer_identity()
    {
        var module = new VrrModule();
        module.CaptureLoops(true);
        int depth = 0;
        NativeClock.LoopAction = _ =>
        {
            if (depth++ == 0)
            {
                long outer = module.CurrentDiagnosticLoop;
                module.CallDiagnosticLoop(0.25f);
                Assert.Equal(outer, module.CurrentDiagnosticLoop);
            }
        };
        module.CallDiagnosticLoop(0.125f);
        var exits = module.DiagnosticEvents.Where(e => e.Kind == "loop_exit").ToArray();
        Assert.Equal(2, exits.Length);
        Assert.NotEqual(exits[0].Loop, exits[1].Loop);
        var inner = JsonSerializer.SerializeToElement(exits[0].Data);
        var outer = JsonSerializer.SerializeToElement(exits[1].Data);
        Assert.True(outer.TryGetProperty("native_start_ticks", out JsonElement start), "Nested outer interval must be measured");
        Assert.True(start.GetInt64() <= inner.GetProperty("native_start_ticks").GetInt64());
        Assert.True(outer.GetProperty("native_end_ticks").GetInt64() >= inner.GetProperty("native_end_ticks").GetInt64());
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disabled_capture_keeps_native_return_and_exception_without_events(bool throws)
    {
        var module = new VrrModule();
        module.CaptureLoops(false);
        int calls = 0;
        NativeClock.LoopAction = _ => { calls++; if (throws) throw new InvalidOperationException("native fixture"); };
        if (throws) Assert.Throws<InvalidOperationException>(() => module.CallDiagnosticLoop(0.125f));
        else module.CallDiagnosticLoop(0.125f);
        Assert.Equal(1, calls);
        Assert.Empty(module.DiagnosticEvents);
        Assert.Equal(0, module.CurrentDiagnosticLoop);
    }
}

public unsafe sealed partial class VrrModule
{
    private const nint DiagnosticFinalize = 0;
    private long _load_passes;
    internal readonly List<(string Kind, object Data, long Loop, int Thread)> DiagnosticEvents = [];
    internal Action? DiagnosticCleanup;
    internal readonly List<(HookSiteId Site, string Name, nint Rva, byte[] Expected)> DiagnosticHookChecks = [];
    internal byte[] PadPollEntry = Convert.FromHexString("558BEC83EC088B4D0853568B750C57");
    internal bool RejectPadObservationHook;
    private bool diagnostic_hook(HookSiteId site, string name, nint rva, byte[] expected, Func<bool> install)
    {
        DiagnosticHookChecks.Add((site, name, rva, expected));
        return ensure_test_hook_registration() && register_diagnostic_hook(site, name, rva,
            rva != 0x489050 || expected.AsSpan().SequenceEqual(PadPollEntry),
            () => (rva != 0x489050 || !RejectPadObservationHook) && install());
    }
    private void diagnostic_raw(byte channel, float value) { }
    internal void CaptureLoops(bool capturing) => _diagnostics = new() { IsCapturing = capturing };
    internal void CallDiagnosticLoop(float delta) => h_diag_loop(delta);
    internal long CurrentDiagnosticLoop => _diagnosticLoopId;
    internal static void ClearDiagnosticLoop() => _diagnosticLoopId = 0;
}

internal static partial class NativeClock
{
    internal static Action<float>? LoopAction;
    public static void Loop(float delta) => LoopAction?.Invoke(delta);
}
