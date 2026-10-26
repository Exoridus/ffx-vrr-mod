namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint DiagnosticPadPoll = 0x489a80;
    private readonly PadObservationTimeline _padTimeline = new(Stopwatch.Frequency);
    private readonly object _padObservationGate = new();
    private object? _padObservationCapture;
    private long _padObservedDropped;
    private long _padObservationSequence;
    private int _padObservationFailed;

    private void init_pad_observations()
    {
        if (_diagnostics?.IsCapturing != true) return;
        bool available = fx_code_matches(PadRecordBaseSite, "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3")
            && fx_operand_is(PadRecordBaseSite + 0xd, PadRecords);
        diagnostic_event("probe_binding", new { name = "pad_records", available });
        if (!available) return;
        diagnostic_hook(HookSiteId.DiagnosticBindingsPadPollObservation, "pad_poll", DiagnosticPadPoll,
            [0x56, 0x33, 0xf6, 0x6a, 0x00, 0x56, 0xe8, 0x85, 0xf3, 0xff, 0xff, 0x50, 0x6a, 0x00, 0x56,
             0xe8, 0xbc, 0xf5, 0xff, 0xff, 0x46, 0x83, 0xc4, 0x14, 0x83, 0xfe, 0x02, 0x7c, 0xe6, 0x5e, 0xc3],
            () => new FhCallHandle<d_render_void>(new FhCallLocation(DiagnosticPadPoll, 0)).hook(this, h_diag_pad_poll));
    }

    private void reset_pad_observation_timeline(string reason)
    {
        lock (_padObservationGate) _padTimeline.Reset(reason);
    }

    // A diagnostic epoch excludes observed discontinuities; it does not prove native input continuity.
    private bool pad_observation_context(bool recording)
    {
        var session = _diagnostics;
        string? reason = session?.IsCapturing != true ? "capture_inactive"
            : System.Threading.Volatile.Read(ref _padObservationFailed) != 0 ? "observer_failed"
            : !OwnsRenderPass || _presentationAdmission.Failure is not null ? "unowned_or_native"
            : !pad_input_presentation_ready() ? "input_group_unready"
            : pad_native_input_owner() ? "native_input_owner" : null;
        if (reason is not null) { _padTimeline.Reset(reason); return false; }
        long dropped = session!.DroppedEvents;
        if (!ReferenceEquals(_padObservationCapture, session))
        {
            _padObservationCapture = session;
            _padObservedDropped = dropped;
            _padTimeline.Reset("capture_changed");
            return recording;
        }
        if (dropped != _padObservedDropped)
        {
            _padObservedDropped = dropped;
            _padTimeline.Reset("capture_loss");
            return false;
        }
        return true;
    }

    private void stop_pad_observations(Exception error)
    {
        reset_pad_observation_timeline("observer_failed");
        if (System.Threading.Interlocked.Exchange(ref _padObservationFailed, 1) == 0)
            diagnostic_event("pad_observation_stopped", new { error = $"{error.GetType().Name}: {error.Message}" });
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_diag_pad_poll()
    {
        try
        {
            new FhCallHandle<d_render_void>(new FhCallLocation(DiagnosticPadPoll, 0)).chain_from(h_diag_pad_poll).fnptr!();
        }
        catch { reset_pad_observation_timeline("native_poll_exception"); throw; }
        if (_diagnostics?.IsCapturing != true || System.Threading.Volatile.Read(ref _padObservationFailed) != 0)
        {
            reset_pad_observation_timeline("capture_inactive_or_failed");
            return;
        }
        try
        {
            long returned = Stopwatch.GetTimestamp();
            long sequence = System.Threading.Interlocked.Increment(ref _padObservationSequence);
            bool eligible;
            long epoch;
            lock (_padObservationGate)
            {
                eligible = pad_observation_context(true);
                epoch = _padTimeline.Epoch;
            }
            (long Start, long End, uint[]? Words) first = default, second = default;
            Span<byte> snapshot = stackalloc byte[32];
            for (int port = 0; port < 2; port++)
            {
                if (_diagnostics?.IsCapturing != true || System.Threading.Volatile.Read(ref _padObservationFailed) != 0)
                {
                    reset_pad_observation_timeline("capture_inactive_or_failed");
                    return;
                }
                long start = Stopwatch.GetTimestamp();
                bool valid = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100 + 0x80), snapshot);
                long end = Stopwatch.GetTimestamp();
                uint[]? words = valid ? MemoryMarshal.Cast<byte, uint>(snapshot).ToArray() : null;
                if (port == 0) first = (start, end, words); else second = (start, end, words);
                diagnostic_event("pad_observation", new { poll_sequence = sequence, port, stage = "post_poll",
                    poll_returned_ticks = returned, observation_start_ticks = start, observation_end_ticks = end,
                    valid, raw_words = words });
            }
            lock (_padObservationGate)
            {
                if (!eligible || !pad_observation_context(false)) return;
                if (epoch != _padTimeline.Epoch) { _padTimeline.Reset("interleaved_observation"); return; }
                if (first.Words is null || second.Words is null) { _padTimeline.Reset("invalid_observation"); return; }
                if (_padTimeline.Observe(sequence, 0, first.Start, first.End, first.Words))
                    _padTimeline.Observe(sequence, 1, second.Start, second.End, second.Words);
            }
        }
        catch (Exception error) { stop_pad_observations(error); }
    }

    private object? pad_observations_pass_completed(double? deadlineSeconds)
    {
        try
        {
            lock (_padObservationGate)
            {
                if (!pad_observation_context(false)) return null;
                if (!_render_clock_seen || !_simulationClock.HasMappedInterval)
                {
                    _padTimeline.Reset("unmapped_clock");
                    return null;
                }
                if (deadlineSeconds is null) return null;
                var result = _padTimeline.Correlate(deadlineSeconds.Value);
                return new { status = "candidate_only", native_input_applied = false, native_continuity_proven = false,
                    epoch = result.Epoch, boundary = result.Boundary,
                    observations = result.Observations, latest = result.Latest };
            }
        }
        catch (Exception error) { stop_pad_observations(error); return null; }
    }
}