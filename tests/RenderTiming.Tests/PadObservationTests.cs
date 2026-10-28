using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public unsafe sealed class PadObservationTests : IDisposable
{
    private readonly byte[] _records = GC.AllocateArray<byte>(0x200, pinned: true);
    private readonly VrrModule _module = new();

    public PadObservationTests()
    {
        Array.Fill(_records, (byte)0xa5);
        fixed (byte* p = _records) FhUtil.PadRecordPointer = (nint)p;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
        NativeClock.PollCalls = 0;
        NativeClock.Hooks.Clear();
        _module.CaptureLoops(true);
    }

    public void Dispose()
    {
        FhUtil.PadRecordPointer = 0;
        NativePadObservation.Reset();
        NativeClock.PollAction = null;
        NativeClock.Hooks.Clear();
    }

    [Fact]
    public void Native_port_polls_return_once_each_before_their_bit_exact_observations()
    {
        var nativeEnds = new List<long>();
        NativeClock.PollAction = () =>
        {
            for (int port = 0; port < 2; port++)
                for (int i = 0; i < 8; i++)
                    BitConverter.TryWriteBytes(_records.AsSpan(port * 0x100 + 0x80 + i * 4, 4), 0x80000000u + (uint)(port * 16 + i));
            nativeEnds.Add(Stopwatch.GetTimestamp());
        };
        _module.CallPadPoll();
        Assert.Equal(2, NativeClock.PollCalls);
        Assert.Equal(2, NativePadObservation.Reads.Count);
        var events = _module.DiagnosticEvents.Where(e => e.Kind == "pad_observation").ToArray();
        Assert.Equal(2, events.Length);
        long? sequence = null;
        long? returned = null;
        for (int port = 0; port < 2; port++)
        {
            JsonElement data = JsonSerializer.SerializeToElement(events[port].Data);
            Assert.Equal(port, data.GetProperty("port").GetInt32());
            Assert.Equal("post_port_poll", data.GetProperty("stage").GetString());
            Assert.True(data.GetProperty("valid").GetBoolean());
            uint[] words = data.GetProperty("raw_words").EnumerateArray().Select(v => v.GetUInt32()).ToArray();
            Assert.Equal(Enumerable.Range(0, 8).Select(i => 0x80000000u + (uint)(port * 16 + i)), words);
            long currentSequence = data.GetProperty("poll_sequence").GetInt64();
            Assert.True(currentSequence > 0);
            if (sequence.HasValue) Assert.True(currentSequence > sequence.Value);
            sequence = currentSequence;
            long pollReturn = data.GetProperty("poll_returned_ticks").GetInt64();
            Assert.True(pollReturn >= nativeEnds[port]);
            if (returned.HasValue) Assert.True(pollReturn >= returned.Value);
            returned = pollReturn;
            long start = data.GetProperty("observation_start_ticks").GetInt64();
            long end = data.GetProperty("observation_end_ticks").GetInt64();
            var read = NativePadObservation.Reads[port];
            Assert.Equal(FhUtil.PadRecordPointer + port * 0x100 + 0x80, read.Address);
            Assert.Equal(32, read.Length);
            Assert.True(pollReturn <= start && start <= read.Start && read.End <= end);
        }
        for (int i = 0; i < _records.Length; i++)
            if (i % 0x100 < 0x80 || i % 0x100 >= 0xa0) Assert.Equal(0xa5, _records[i]);
        byte[] afterNative = (byte[])_records.Clone();
        NativeClock.PollAction = null;
        _module.CallPadPoll();
        Assert.Equal(afterNative, _records);
        var last = JsonSerializer.SerializeToElement(_module.DiagnosticEvents.Last().Data);
        Assert.True(last.GetProperty("poll_sequence").GetInt64() > sequence);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(-1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 1)]
    public void Per_port_forwarding_preserves_arguments_and_samples_only_supported_identity(int port, int pad)
    {
        _module.CallPadPortPoll(port, pad, (nint)0x12345678);
        Assert.Equal((port, pad, (nint)0x12345678), NativeClock.LastPollArguments);
        Assert.Equal(1, NativeClock.PollCalls);
        if (port is >= 0 and <= 1 && pad == 0)
            Assert.Equal(FhUtil.PadRecordPointer + port * 0x100 + 0x80, Assert.Single(NativePadObservation.Reads).Address);
        else
        {
            Assert.Empty(NativePadObservation.Reads);
            Assert.Empty(_module.DiagnosticEvents);
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inactive_or_absent_capture_only_forwards_native_poll(bool absent)
    {
        if (absent) _module.RemovePadCapture();
        else _module.CaptureLoops(false);
        _module.CallPadPoll();
        Assert.Equal(2, NativeClock.PollCalls);
        Assert.Empty(NativePadObservation.Reads);
        Assert.Empty(_module.DiagnosticEvents);
    }

    [Fact]
    public void Unreadable_port_is_explicitly_invalid_and_keeps_other_port()
    {
        NativePadObservation.FailedPort = 0;
        _module.CallPadPoll();
        var events = _module.DiagnosticEvents.Where(e => e.Kind == "pad_observation").ToArray();
        Assert.Equal(2, events.Length);
        var failed = JsonSerializer.SerializeToElement(events[0].Data);
        Assert.False(failed.GetProperty("valid").GetBoolean());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("raw_words").ValueKind);
        Assert.True(JsonSerializer.SerializeToElement(events[1].Data).GetProperty("valid").GetBoolean());
        Assert.Equal(2, NativeClock.PollCalls);
    }

    [Fact]
    public void Sampling_exception_stops_observer_once_and_future_polls_still_forward()
    {
        NativePadObservation.ThrowOnCopy = true;
        _module.CallPadPoll();
        int reads = NativePadObservation.Reads.Count;
        Assert.Equal(1, reads);
        _module.CallPadPoll();
        Assert.Equal(4, NativeClock.PollCalls);
        Assert.Equal(reads, NativePadObservation.Reads.Count);
        Assert.Single(_module.DiagnosticEvents, e => e.Kind == "pad_observation_stopped");
        Assert.DoesNotContain(_module.DiagnosticEvents, e => e.Kind == "pad_observation");
    }

    [Fact]
    public void Native_exception_is_preserved_without_any_observation()
    {
        var error = new InvalidOperationException("native poll fixture");
        NativeClock.PollAction = () => throw error;
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => _module.CallPadPoll()));
        Assert.Equal(1, NativeClock.PollCalls);
        Assert.Empty(NativePadObservation.Reads);
        Assert.Empty(_module.DiagnosticEvents);
    }

    [Fact]
    public void Foreign_thread_forwards_and_records_its_own_thread()
    {
        int caller = Environment.CurrentManagedThreadId, worker = 0;
        Exception? failure = null;
        _module.SetHookScope(true, true, true, false);
        var thread = new Thread(() =>
        {
            try { worker = Environment.CurrentManagedThreadId; _module.CallPadPoll(); }
            catch (Exception e) { failure = e; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.NotEqual(caller, worker);
        Assert.Equal(2, NativeClock.PollCalls);
        Assert.Equal(2, _module.DiagnosticEvents.Count);
        Assert.All(_module.DiagnosticEvents, e => Assert.Equal(worker, e.Thread));
    }

    [Fact]
    public void Initializer_registers_declared_diagnostic_site_with_complete_guard_while_independent_is_disabled()
    {
        Assert.False(_module.Effective);
        _module.InitPadObservation();
        var check = Assert.Single(_module.DiagnosticHookChecks);
        Assert.True(HookSites.IsDeclared(check.Site));
        Assert.Equal("DiagnosticBindingsPadPollObservation", check.Site.ToString());
        Assert.Equal(0x489050, check.Rva);
        Assert.Equal(Convert.FromHexString("558BEC83EC088B4D0853568B750C57"), check.Expected);
        Assert.Single(NativeClock.Hooks);
        Assert.Contains(_module.CodeChecks, c => c.Rva == 0x488ec0 && c.Pattern == "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3");
        Assert.Contains(_module.OperandChecks, c => c.Rva == 0x488ecd && c.Expected == 0xf30288);
        Assert.Equal(21, (int)HookSiteId.DiagnosticBindingsMainLoop);
        Assert.True((int)check.Site > (int)HookSiteId.VideoOverridePhyFMVPlayerManagerPlayStart);
    }

    [Fact]
    public void Inactive_capture_initializer_performs_no_preflight_or_registration()
    {
        _module.CaptureLoops(false);
        _module.InitPadObservation();
        Assert.Empty(_module.CodeChecks);
        Assert.Empty(_module.OperandChecks);
        Assert.Empty(_module.DiagnosticHookChecks);
        Assert.Empty(NativeClock.Hooks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Changed_getter_code_or_relocated_operand_prevents_registration(bool code, bool operand)
    {
        _module.PadGetterCodeMatches = code;
        _module.PadGetterOperandMatches = operand;
        _module.InitPadObservation();
        Assert.Empty(NativeClock.Hooks);
        Assert.Empty(_module.DiagnosticHookChecks);
        var binding = Assert.Single(_module.DiagnosticEvents, e => e.Kind == "probe_binding");
        Assert.False(JsonSerializer.SerializeToElement(binding.Data).GetProperty("available").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(GuardBytePositions))]
    public void Any_changed_poll_guard_byte_prevents_registration(int position)
    {
        _module.PadPollEntry[position] ^= 0xff;
        _module.InitPadObservation();
        Assert.Single(_module.DiagnosticHookChecks);
        Assert.Empty(NativeClock.Hooks);
    }

    public static IEnumerable<object[]> GuardBytePositions() => Enumerable.Range(0, 15).Select(i => new object[] { i });
}

public unsafe sealed partial class VrrModule
{
    internal void RemovePadCapture() => _diagnostics = null;
    internal void CallPadPortPoll(int port, int pad, nint buffer) => h_diag_pad_poll(port, pad, buffer);
    internal void CallPadPoll() { h_diag_pad_poll(0, 0, 0); h_diag_pad_poll(1, 0, 0); }
    internal void InitPadObservation() => init_pad_observations();
}

internal static partial class NativeClock
{
    internal static Action? PollAction;
    internal static int PollCalls;
    internal static (int Port, int Pad, nint Buffer) LastPollArguments;
    public static void Poll(int port, int pad, nint buffer) { LastPollArguments = (port, pad, buffer); Interlocked.Increment(ref PollCalls); PollAction?.Invoke(); }
}

internal static unsafe class NativePadObservation
{
    internal static readonly List<(nint Address, int Length, long Start, long End)> Reads = [];
    internal static int? FailedPort;
    internal static bool ThrowOnCopy;
    internal static Action? AfterCopy;
    internal static void Reset() { Reads.Clear(); FailedPort = null; ThrowOnCopy = false; AfterCopy = null; }
    internal static bool Copy(nint address, Span<byte> target)
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            Assert.Equal(32, target.Length);
            if (ThrowOnCopy) throw new InvalidOperationException("observation fixture");
            if (FailedPort == (int)((address - FhUtil.PadRecordPointer - 0x80) / 0x100)) return false;
            new ReadOnlySpan<byte>((void*)address, 32).CopyTo(target);
            AfterCopy?.Invoke();
            return true;
        }
        finally { Reads.Add((address, target.Length, start, Stopwatch.GetTimestamp())); }
    }
}