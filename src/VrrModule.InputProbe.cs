namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private bool _inputProbeInitialized, _inputProbeEnabled;
    private readonly object _inputProbeGate = new();
    private long _inputProbeRoot, _inputProbeClock, _inputProbeClockSequence;
    private int _inputProbeThread, _inputProbeLatchCalls, _inputProbeSnapshotCalls,
        _inputProbeClockCalls, _inputProbeClockReturns;
    private bool _inputProbeRootInvalid;
    private bool InputProbeEnabled => _inputProbeEnabled;

    private void init_input_probe()
    {
        if (_inputProbeInitialized) return;
        _inputProbeInitialized = true;
        _inputProbeEnabled = Environment.GetEnvironmentVariable("FFX_VRR_INPUT_PROBE") == "1"
            && _config.NativePhaseProbe && _diagnostics?.IsCapturing == true
            && ChosenFramerate == 60d && !IndependentPresentationEnabled;
    }

    private long input_probe_enter_loop(PadSnapshotScope scope)
    {
        if (!InputProbeEnabled || !RuntimeEffectsArmed || _diagnostics?.IsCapturing != true) return 0;
        lock (_inputProbeGate)
        {
            if (!scope.Owned || !scope.Root || _inputProbeRoot != 0)
            {
                if (_inputProbeRoot != 0) _inputProbeRootInvalid = true;
                return 0;
            }
            _inputProbeRoot = scope.Invocation;
            _inputProbeThread = Environment.CurrentManagedThreadId;
            _inputProbeClock = 0;
            _inputProbeLatchCalls = _inputProbeSnapshotCalls = _inputProbeClockCalls = _inputProbeClockReturns = 0;
            _inputProbeRootInvalid = false;
            return _inputProbeRoot;
        }
    }

    private void input_probe_exit_loop(long scope, bool nativeReturned)
    {
        if (scope == 0) return;
        object result;
        lock (_inputProbeGate)
        {
            if (scope != _inputProbeRoot || _inputProbeThread != Environment.CurrentManagedThreadId) return;
            result = new { root = scope, normal_return = nativeReturned, invalid = _inputProbeRootInvalid,
                clock_calls = _inputProbeClockCalls, clock_returns = _inputProbeClockReturns,
                latch_calls = _inputProbeLatchCalls, snapshot_calls = _inputProbeSnapshotCalls,
                open_clock = _inputProbeClock, managed_thread = _inputProbeThread };
            _inputProbeRoot = _inputProbeClock = 0;
            _inputProbeThread = 0;
        }
        input_probe_event("input_probe_root", result);
    }

    private long input_probe_enter_clock()
    {
        if (!InputProbeEnabled || !RuntimeEffectsArmed || _diagnostics?.IsCapturing != true) return 0;
        lock (_inputProbeGate)
        {
            if (_inputProbeRoot == 0 || _inputProbeThread != Environment.CurrentManagedThreadId) return 0;
            if (_inputProbeClock != 0) { _inputProbeRootInvalid = true; return 0; }
            _inputProbeClock = ++_inputProbeClockSequence;
            _inputProbeClockCalls++;
            return _inputProbeClock;
        }
    }

    private void input_probe_exit_clock(long token, bool normal)
    {
        if (token == 0) return;
        lock (_inputProbeGate)
        {
            if (_inputProbeClock != token || _inputProbeThread != Environment.CurrentManagedThreadId) return;
            if (normal) _inputProbeClockReturns++;
            else _inputProbeRootInvalid = true;
            _inputProbeClock = 0;
        }
    }

    private void input_probe_latch_invoke(nuint returnAddress, nuint returnSlot, d_render_void original)
        => input_probe_invoke(false, returnAddress, returnSlot, original);

    private void input_probe_snapshot_invoke(nuint returnAddress, nuint returnSlot, d_render_void original)
        => input_probe_invoke(true, returnAddress, returnSlot, original);

    private void input_probe_invoke(bool snapshot, nuint returnAddress, nuint returnSlot, d_render_void original)
    {
        if (!InputProbeEnabled || !RuntimeEffectsArmed || _diagnostics?.IsCapturing != true) { original(); return; }
        object before = input_probe_memory();
        long root, clock;
        bool owned;
        lock (_inputProbeGate)
        {
            root = _inputProbeRoot;
            owned = root != 0 && _inputProbeThread == Environment.CurrentManagedThreadId;
            clock = owned ? _inputProbeClock : 0;
            if (owned)
            {
                if (snapshot) _inputProbeSnapshotCalls++;
                else _inputProbeLatchCalls++;
            }
        }
        bool slotMatches = false;
        string callerKind = "unmatched";
        uint nativeThread = 0;
        try
        {
            nativeThread = input_probe_native_thread();
            slotMatches = returnAddress <= uint.MaxValue && returnSlot <= nuint.MaxValue - sizeof(uint)
                && MemoryProbe.IsReadable((nint)returnSlot, sizeof(uint)) && *(uint*)returnSlot == returnAddress;
            if (snapshot && returnAddress == (nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation)) callerKind = "script_snapshot";
            else if (!snapshot && slotMatches && returnAddress == (nuint)FhUtil.ptr_at<byte>(PadLatchPublicationReturn)) callerKind = "ordinary_clock";
            else if (!snapshot && slotMatches && returnAddress == (nuint)FhUtil.ptr_at<byte>(0x420ba8)) callerKind = "menu_pause";
        }
        catch (Exception) { }
        bool normal = false;
        long start = Stopwatch.GetTimestamp();
        try { original(); normal = true; }
        finally
        {
            long end = Stopwatch.GetTimestamp();
            object after = input_probe_memory();
            input_probe_event(snapshot ? "input_probe_snapshot" : "input_probe_latch",
                new { root, owned, clock_token = clock, managed_thread = Environment.CurrentManagedThreadId,
                    native_thread = nativeThread, return_address = (ulong)returnAddress, return_slot = (ulong)returnSlot,
                    return_slot_matches = slotMatches, caller_kind = callerKind,
                    native_start_ticks = start, native_end_ticks = end, normal_return = normal,
                    before, after, native_input_written = false, simulation_deferred = false });
        }
    }

    private object input_probe_memory()
    {
        try
        {
            long start = Stopwatch.GetTimestamp();
            object[] ports = new object[2];
            Span<byte> record = stackalloc byte[0xa0];
            for (int port = 0; port < ports.Length; port++)
            {
                bool valid = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100), record);
                ports[port] = new { port, valid, head = valid ? (byte?)record[0x9c] : null,
                    raw_words = valid ? MemoryMarshal.Cast<byte, uint>(record.Slice(0x80, 32)).ToArray() : null,
                    history_words = valid ? MemoryMarshal.Cast<byte, uint>(record.Slice(0, 0x80)).ToArray() : null };
            }
            Span<byte> script = stackalloc byte[sizeof(ushort)];
            bool scriptValid = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(0xf270c0), script);
            return new { observation_start_ticks = start, observation_end_ticks = Stopwatch.GetTimestamp(), ports,
                script_valid = scriptValid, script_word = scriptValid ? (ushort?)MemoryMarshal.Read<ushort>(script) : null,
                menu_pause = FhUtil.get_at<int>(PadMenuPause), main_menu = FhUtil.get_at<int>(PadMainMenu),
                menu_reservation = FhUtil.get_at<int>(PadMenuReservation), overdrive = FhUtil.get_at<int>(OverdriveActive),
                dummy = FhUtil.get_at<byte>(PadDummyActive), syncdata = EngineSyncPaced, movie = fmv_playing() };
        }
        catch (Exception error) { return new { error = error.GetType().Name }; }
    }

    private void input_probe_event(string kind, object data)
    {
        try { if (_diagnostics?.IsCapturing == true) diagnostic_event(kind, data); }
        catch (Exception) { }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint input_probe_native_thread();
}
