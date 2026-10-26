using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadLatchOwnershipTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private readonly VrrModule _module = new();


    public PadLatchOwnershipTests()
    {
        for (int i = 0; i < _records.Length; i++) _records[i] = (byte)(i * 37 + 11);
        _records[0x9c] = 1;
        _records[0x19c] = 3;
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        FhUtil.Overdrive = 0;
        NativeClock.PadCalls = 0;
        _module.ConfigurePad(clockSeen: false, presentation: false);
    }

    public void Dispose()
    {
        _module.EndPad();
        FhUtil.PadRecordPointer = 0;
        FhUtil.Overdrive = 0;
        RenderPassContext.IsPresentation = false;
        RenderPassContext.IsActive = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_latch_forwards_without_owner_pending_or_skip(bool clockSeen)
    {
        _module.ConfigurePad(clockSeen, presentation: true);
        var state = _module.PadState;
        byte[] before = (byte[])_records.Clone();
        Foreign(_module.CallPad);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(state, _module.PadState);
        Assert.Equal(before, _records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_decision_cannot_consume_pending_or_change_history(bool presentation)
    {
        _module.CallPad();
        Assert.True(_module.PadState.Pending);
        var state = _module.PadState;
        byte[] before = (byte[])_records.Clone();
        Foreign(() => _module.DecidePad(presentation));
        Assert.Equal(0, NativeClock.PadCalls);
        Assert.Equal(state, _module.PadState);
        Assert.Equal(before, _records);
        _module.DecidePad(false);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(_module.PadState.Pending);
    }

    [Fact]
    public void Foreign_cleanup_cannot_flush_owner_pending()
    {
        _module.CallPad();
        var state = _module.PadState;
        Foreign(_module.EndPad);
        Assert.Equal(0, NativeClock.PadCalls);
        Assert.Equal(state, _module.PadState);
        _module.EndPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, _module.PadState.Late);
    }

    [Fact]
    public void Foreign_cleanup_preserves_owner_history()
    {
        byte[] before = (byte[])_records.Clone();
        _module.DecidePad(true);
        var state = _module.PadState;
        byte[] afterDecision = (byte[])_records.Clone();
        Assert.Equal(before, afterDecision);
        Foreign(_module.EndPad);
        Assert.Equal(state, _module.PadState);
        Assert.Equal(afterDecision, _records);
        _module.EndPad();
        Assert.Equal(before, _records);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owner_decision_publishes_only_simulation_and_preserves_history(bool presentation)
    {
        byte[] before = (byte[])_records.Clone();
        _module.CallPad();
        Assert.Equal(0, NativeClock.PadCalls);
        Assert.True(_module.PadState.Pending);
        _module.DecidePad(presentation);
        Assert.False(_module.PadState.Pending);
        Assert.Equal(presentation ? 0 : 1, NativeClock.PadCalls);
        Assert.Equal(before, _records);
        _module.EndPad();
        Assert.Equal(before, _records);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Outside_owned_pass_latches_native(bool armed, bool frame, bool inside)
    {
        _module.ConfigurePad(false, true, armed, frame, inside);
        var state = _module.PadState;
        _module.CallPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(state, _module.PadState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owner_latch_after_clock_publishes_only_simulation(bool presentation)
    {
        _module.ConfigurePad(true, presentation);
        _module.CallPad();
        Assert.Equal(presentation ? 0 : 1, NativeClock.PadCalls);
        Assert.Equal(presentation ? 1 : 0, _module.PadState.Skipped);
        Assert.False(_module.PadState.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Overdrive_latches_native_without_deferral_or_skip(bool clockSeen)
    {
        _module.ConfigurePad(clockSeen, true);
        FhUtil.Overdrive = 1;
        var state = _module.PadState;
        _module.CallPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(state, _module.PadState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Owner_cleanup_preserves_history_after_frame_or_readiness_revocation(bool disarm)
    {
        byte[] before = (byte[])_records.Clone();
        _module.DecidePad(true);
        Assert.Equal(before, _records);
        _module.RevokePadFrame(disarm);
        _module.EndPad();
        Assert.Equal(before, _records);
    }

    [Fact]
    public void Native_handover_decision_publishes_the_owners_already_deferred_latch()
    {
        _module.CallPad();
        _module.RevokePadFrame(false);
        _module.DecidePad(false);
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.False(_module.PadState.Pending);
    }

    [Fact]
    public void Cleanup_preserves_native_push_and_cleared_edges()
    {
        byte[] before = (byte[])_records.Clone();
        _module.DecidePad(true);
        int edge = 0x20 + 6;
        _records[edge] = 0;
        _records[edge + 1] = 0;
        _records[0x9c] = 2;
        _module.EndPad();
        before[edge] = 0;
        before[edge + 1] = 0;
        before[0x9c] = 2;
        Assert.Equal(before, _records);
    }

    [Fact]
    public void No_clock_owner_cleanup_publishes_deferred_latch_once()
    {
        _module.CallPad();
        _module.EndPad();
        _module.EndPad();
        Assert.Equal(1, NativeClock.PadCalls);
        Assert.Equal(1, _module.PadState.Late);
    }

    private static void Foreign(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
    }
}

public unsafe sealed partial class VrrModule
{
    internal void ConfigurePad(bool clockSeen, bool presentation, bool armed = true, bool frame = true, bool inside = true)
    {
        SetHookScope(armed, frame, inside, presentation);
        _pad_latch_installed = _pad_history_readers_ready = true;
        _render_clock_seen = clockSeen;
    }
    internal (bool Pending, long Skipped, long Deferred, long Late) PadState
        => (_pad_latch_pending, _pad_latch_skipped, _pad_latch_deferred, _pad_latch_late);
    internal void CallPad() => h_pad_latch();
    internal void DecidePad(bool presentation) => pad_latch_pass_decided(presentation);
    internal void EndPad() => pad_latch_end_pass();
    internal void RevokePadFrame(bool disarm) { _render_frame_enabled = false; if (disarm) _render_mode_ready = false; }
    internal bool PadGetterCodeMatches = true, PadGetterOperandMatches = true;
    internal nint? RejectedPadCodeSite, RejectedPadOperandSite;
    internal bool RejectPadOverdriveCall;
    internal readonly List<(nint Rva, nint Expected)> CallChecks = [];
    internal readonly List<(nint Rva, string Pattern)> CodeChecks = [];
    internal readonly List<(nint Rva, nint Expected)> OperandChecks = [];
    private bool fx_code_matches(nint rva, string pattern)
    {
        CodeChecks.Add((rva, pattern));
        if (rva == RejectedPadCodeSite) return false;
        if (PadHistoryEntries.TryGetValue(rva, out byte[]? entry))
            return entry.AsSpan().SequenceEqual(Convert.FromHexString(pattern.Replace(" ", "")));
        return rva != 0x488ec0 || PadGetterCodeMatches;
    }
    private bool fx_operand_is(nint rva, nint expected)
    {
        OperandChecks.Add((rva, expected));
        return rva != RejectedPadOperandSite && (rva != 0x488ecd || PadGetterOperandMatches);
    }
    private bool fx_call_is(nint rva, nint expected) { CallChecks.Add((rva, expected)); return !RejectPadOverdriveCall; }
}

internal static partial class NativeClock
{
    internal static int PadCalls;
    public static void Pad() => Interlocked.Increment(ref PadCalls);
}
