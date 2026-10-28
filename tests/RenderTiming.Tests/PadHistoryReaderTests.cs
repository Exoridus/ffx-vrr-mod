using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadHistoryReaderTests : IDisposable
{
    private readonly VrrModule _module = new();
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private static readonly nint[] Readers = [0x488e80, 0x488ea0, 0x488e50, 0x488e30];
    private static readonly int[] Edges = [6, 8, 10, 28];

    public PadHistoryReaderTests()
    {
        _module.EnableAdapter();
        _module.ResetHistoryStartup();
        NativeClock.HistoryHookRegistrations.Clear();
        NativeClock.HistoryCalls.Clear();
        NativeClock.Hooks.Clear();
        NativeClock.HistoryRead = null;
        NativeClock.HistoryHookAction = null;
        NativeClock.HistoryValue = 0x8000;
        NativeClock.PadCalls = 0;
        FhUtil.PadRecordPointer = 0;
        FhUtil.Overdrive = 0;
        _module.SetHookScope(true, true, true, true);
    }

    public void Dispose()
    {
        _module.EndPad();
        NativeClock.HistoryRead = null;
        NativeClock.HistoryHookAction = null;
        NativeClock.HistoryHookRegistrations.Clear();
        NativeClock.HistoryCalls.Clear();
        NativeClock.Hooks.Clear();
        FhUtil.PadRecordPointer = 0;
        FhUtil.Overdrive = 0;
        RenderPassContext.IsPresentation = RenderPassContext.IsActive = false;
    }

    [Fact]
    public void Full_group_preflights_before_first_registration_and_arms_only_when_complete()
    {
        NativeClock.HistoryHookAction = () =>
        {
            Assert.False(_module.HistoryReady);
            Assert.Equal(15, _module.CodeChecks.Count);
            Assert.Equal(19, _module.OperandChecks.Count);
            Assert.Equal(9, _module.CallChecks.Count);
            for (int kind = 0; kind < 4; kind++) Assert.Equal(0x8000, _module.ReadHistory(kind, 0, 0, -1));
            int before = NativeClock.PadCalls;
            _module.CallPad();
            Assert.Equal(before + 1, NativeClock.PadCalls);
            Assert.False(_module.PadState.Pending);
        };
        Assert.True(_module.InitHistory());
        Assert.True(_module.HistoryReady);
        Assert.Equal(new nint[] { 0x4893e0, 0x488e80, 0x488ea0, 0x488e50, 0x488e30, 0x4be500, 0x4be4b0, 0x4be470, 0x42d070 }, NativeClock.HistoryHookRegistrations.Select(h => h.Rva));
        Assert.Equal(new[] { "d_render_void", "d_pad_history", "d_pad_history", "d_pad_history", "d_pad_history", "d_render_void", "d_menu_edge", "d_menu_edge", "d_render_void" }, NativeClock.Hooks);
        Assert.Equal(21, (int)HookSiteId.DiagnosticBindingsMainLoop);
        Assert.Equal(Enumerable.Range(140, 4), new[] { (int)HookSiteId.PadLatchPressedHistory, (int)HookSiteId.PadLatchReleasedHistory, (int)HookSiteId.PadLatchRepeatHistory, (int)HookSiteId.PadLatchRepeat2History });
        Assert.All(NativeClock.HistoryHookRegistrations, h => Assert.Equal("Cdecl", h.Callback.GetType().GetCustomAttributes(typeof(UnmanagedFunctionPointerAttribute), false).Cast<UnmanagedFunctionPointerAttribute>().Single().CallingConvention.ToString()));
    }

    [Theory]
    [MemberData(nameof(ReaderGuardBytes))]
    public void Every_reader_guard_byte_is_checked_before_any_hook(int kind, int position)
    {
        _module.PadHistoryEntries[Readers[kind]][position] ^= 0xff;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
        Assert.Equal(0x8000, _module.ReadHistory(kind, 0, 0, -1));
    }

    public static IEnumerable<object[]> ReaderGuardBytes()
        => from kind in Enumerable.Range(0, 4) from position in Enumerable.Range(0, 26) select new object[] { kind, position };

    [Theory]
    [InlineData(0x4893e0, false, false)]
    [InlineData(0x488ec0, false, false)]
    [InlineData(0x49ae52, false, false)]
    [InlineData(0x488ecd, true, false)]
    [InlineData(0x49ae54, true, false)]
    [InlineData(0x49ae65, false, true)]
    public void Existing_latch_record_and_overdrive_guards_fail_before_registration(int site, bool operand, bool call)
    {
        if (call) _module.RejectPadOverdriveCall = true;
        else if (operand) _module.RejectedPadOperandSite = site;
        else _module.RejectedPadCodeSite = site;
        Assert.True(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }

    [Fact]
    public void Disabled_independent_presentation_does_not_preflight_or_install()
    {
        var module = new VrrModule();
        Assert.True(module.InitHistory());
        Assert.False(module.HistoryReady);
        Assert.Empty(module.CodeChecks);
        Assert.Empty(NativeClock.HistoryHookRegistrations);
    }

    [Theory]
    [InlineData("pad latch")]
    [InlineData("pad pressed history")]
    [InlineData("pad released history")]
    [InlineData("pad repeat history")]
    [InlineData("pad repeat2 history")]
    public void Partial_registration_failure_preserves_native_getters_and_latch(string rejected)
    {
        _module.RejectedHook = rejected;
        Assert.False(_module.InitHistory());
        Assert.False(_module.HistoryReady);
        for (int kind = 0; kind < 4; kind++) Assert.Equal(0x8000, _module.ReadHistory(kind, -4, 9, -3));
        _module.CallPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(_module.PadState.Pending);
    }

    [Theory]
    [MemberData(nameof(ReaderArguments))]
    public void Native_runs_once_with_exact_arguments_and_returns_bit_exact_outside_presentation(int kind, int port, int pad, int history, ushort value)
    {
        Assert.True(_module.InitHistory());
        NativeClock.HistoryValue = value;
        _module.SetHookScope(true, true, true, false);
        Assert.Equal(value, _module.ReadHistory(kind, port, pad, history));
        Assert.Equal((kind, port, pad, history), Assert.Single(NativeClock.HistoryCalls));
        NativeClock.HistoryCalls.Clear();
        _module.SetHookScope(true, true, true, true);
        Assert.Equal((ushort)0, _module.ReadHistory(kind, port, pad, history));
        Assert.Equal((kind, port, pad, history), Assert.Single(NativeClock.HistoryCalls));
    }

    public static IEnumerable<object[]> ReaderArguments()
        => from kind in Enumerable.Range(0, 4)
           from history in new[] { 0, -1, -2, -3, 1, int.MinValue, int.MaxValue }
           from value in new ushort[] { 0, 0xffff, 0x8000 }
           select new object[] { kind, -17, 29, history, value };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Native_exception_is_preserved_before_any_visibility_decision(int kind)
    {
        Assert.True(_module.InitHistory());
        var failure = new InvalidOperationException("native history fixture");
        NativeClock.HistoryRead = (_, _, _, _) => throw failure;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => _module.ReadHistory(kind, 5, 6, -1)));
        Assert.Equal((kind, 5, 6, -1), Assert.Single(NativeClock.HistoryCalls));
    }

    [Theory]
    [InlineData("unready")]
    [InlineData("outside")]
    [InlineData("disarmed")]
    [InlineData("handover")]
    [InlineData("ordinary")]
    [InlineData("overdrive")]
    [InlineData("foreign")]
    public void Nonowned_and_native_states_preserve_all_four_results(string state)
    {
        Assert.True(_module.InitHistory());
        if (state == "unready") _module.ResetHistoryStartup();
        if (state == "outside") _module.SetHookScope(true, true, false, true);
        if (state == "disarmed") _module.SetHookScope(false, true, true, true);
        if (state == "handover") _module.SetHookScope(true, false, true, true);
        if (state == "ordinary") _module.SetHookScope(true, true, true, false);
        if (state == "overdrive") FhUtil.Overdrive = 1;
        Action read = () =>
        {
            for (int kind = 0; kind < 4; kind++) Assert.Equal(0x8000, _module.ReadHistory(kind, 1, 2, -1));
        };
        if (state == "foreign")
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { read(); } catch (Exception e) { failure = e; } });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(failure);
        }
        else read();
        Assert.Equal(4, NativeClock.HistoryCalls.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void Menu_current_and_previous_press_repeat_aggregation_is_hidden_without_writing_history(int port, int pad)
    {
        Assert.True(_module.InitHistory());
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        Array.Fill(_records, (byte)0);
        int record = (port + pad) * 0x100;
        _records[record + 0x9c] = 2;
        SetEdge(record + 0x20 + 6, 0x8000);
        SetEdge(record + 0x20 + 10, 0x4000);
        NativeClock.HistoryRead = (kind, p, d, age) =>
        {
            int start = (p + d) * 0x100;
            int slot = (_records[start + 0x9c] + age) & 3;
            return BitConverter.ToUInt16(_records, start + slot * 0x20 + Edges[kind]);
        };
        byte[] before = (byte[])_records.Clone();
        ushort Menu() => (ushort)(_module.ReadHistory(0, port, pad, 0) | _module.ReadHistory(0, port, pad, -1)
            | _module.ReadHistory(2, port, pad, 0) | _module.ReadHistory(2, port, pad, -1));
        Assert.Equal((ushort)0, Menu());
        Assert.Equal(before, _records);
        _module.SetHookScope(true, true, true, false);
        Assert.Equal(0xc000, Menu());
        Assert.Equal(before, _records);
        Assert.Equal(8, NativeClock.HistoryCalls.Count);
    }

    [Fact]
    public void Native_clear_and_push_during_presentation_survive_cleanup()
    {
        Assert.True(_module.InitHistory());
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        Array.Fill(_records, (byte)0xa5);
        _module.DecidePad(true);
        NativeClock.HistoryRead = (_, _, _, _) =>
        {
            Array.Clear(_records, 0x20, 0x20);
            _records[0x9c] = 3;
            SetEdge(0x60 + 6, 0x1234);
            return 0xffff;
        };
        Assert.Equal((ushort)0, _module.ReadHistory(0, 0, 0, -1));
        byte[] afterNative = (byte[])_records.Clone();
        _module.EndPad();
        Assert.Equal(afterNative, _records);
        Assert.All(_records.AsSpan(0x20, 0x20).ToArray(), b => Assert.Equal(0, b));
    }

    private void SetEdge(int offset, ushort value) => BitConverter.TryWriteBytes(_records.AsSpan(offset, 2), value);
}

public unsafe sealed partial class VrrModule
{
    internal readonly Dictionary<nint, byte[]> PadHistoryEntries = new()
    {
        [0x488e80] = Convert.FromHexString("558BECFF7510FF750CFF7508E8EFFCFFFF668B400683C40C5DC3"),
        [0x488ea0] = Convert.FromHexString("558BECFF7510FF750CFF7508E8CFFCFFFF668B400883C40C5DC3"),
        [0x488e50] = Convert.FromHexString("558BECFF7510FF750CFF7508E81FFDFFFF668B400A83C40C5DC3"),
        [0x488e30] = Convert.FromHexString("558BECFF7510FF750CFF7508E83FFDFFFF668B401C83C40C5DC3"),
    };
    internal void ResetHistoryStartup() => _pad_latch_installed = _pad_history_readers_ready = false;
    internal bool InitHistory() => init_pad_latch_hook();
    internal bool HistoryReady => pad_input_presentation_ready();
    internal ushort ReadHistory(int kind, int port, int pad, int history) => kind switch
    {
        0 => h_pad_pressed_history(port, pad, history),
        1 => h_pad_released_history(port, pad, history),
        2 => h_pad_repeat_history(port, pad, history),
        3 => h_pad_repeat2_history(port, pad, history),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal static partial class NativeClock
{
    internal static readonly List<(nint Rva, Delegate Callback)> HistoryHookRegistrations = [];
    internal static Action? HistoryHookAction;
    internal static readonly List<(int Kind, int Port, int Pad, int History)> HistoryCalls = [];
    internal static ushort HistoryValue;
    internal static Func<int, int, int, int, ushort>? HistoryRead;
    public static ushort PressedHistory(int port, int pad, int history) => ReadHistoryNative(0, port, pad, history);
    public static ushort ReleasedHistory(int port, int pad, int history) => ReadHistoryNative(1, port, pad, history);
    public static ushort RepeatHistory(int port, int pad, int history) => ReadHistoryNative(2, port, pad, history);
    public static ushort Repeat2History(int port, int pad, int history) => ReadHistoryNative(3, port, pad, history);
    private static ushort ReadHistoryNative(int kind, int port, int pad, int history)
    {
        HistoryCalls.Add((kind, port, pad, history));
        return HistoryRead?.Invoke(kind, port, pad, history) ?? HistoryValue;
    }
}
