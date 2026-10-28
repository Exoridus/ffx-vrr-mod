namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint DiagnosticPadPoll = 0x489050;
    private readonly PadObservationTimeline _padTimeline = new(Stopwatch.Frequency);
    private readonly object _padObservationGate = new();
    private object? _padObservationOwner;
    private bool _padObservationAttempted, _padObservationInstalled;
    private long _padObservedDropped;
    private long _padObservationSequence;
    private int _padObservationFailed;
    private PadTickCorrelation? _padPreparedTick;
    private double? _padPreparedDeadline;

    private bool init_pad_observations()
    {
        if (_padObservationAttempted) return !IndependentPresentationEnabled || _padObservationInstalled;
        if (!IndependentPresentationEnabled && (!_config.NativePhaseProbe || _diagnostics?.IsCapturing != true)) return true;
        _padObservationAttempted = true;
        bool available = fx_code_matches(PadRecordBaseSite, "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3")
            && fx_operand_is(PadRecordBaseSite + 0xd, PadRecords);
        try { diagnostic_event("probe_binding", new { name = "pad_records", available }); }
        catch (Exception) { }
        if (!available) return !IndependentPresentationEnabled;
        // The outer two-port loop branches into its first five bytes, which an entry detour overwrites.
        _padObservationInstalled = diagnostic_hook(HookSiteId.DiagnosticBindingsPadPollObservation, "pad_port_poll", DiagnosticPadPoll,
            [0x55, 0x8b, 0xec, 0x83, 0xec, 0x08, 0x8b, 0x4d, 0x08, 0x53, 0x56, 0x8b, 0x75, 0x0c, 0x57],
            () => new FhCallHandle<d_pad_port_poll>(new FhCallLocation(DiagnosticPadPoll, 0)).hook(this, with_hook_continuation<d_pad_port_poll>(h_diag_pad_poll)));
        return !IndependentPresentationEnabled || _padObservationInstalled;
    }

    private bool pad_observation_binding_ready()
        => _padObservationInstalled && System.Threading.Volatile.Read(ref _padObservationFailed) == 0;

    private void reset_pad_observation_timeline(string reason)
    {
        lock (_padObservationGate)
        {
            _padPreparedTick = null;
            _padPreparedDeadline = null;
            _padTimeline.Reset(reason);
        }
    }

    // Active candidate epochs belong to the module; telemetry sessions cannot invalidate them.
    private bool pad_observation_context(bool recording)
    {
        bool required = IndependentPresentationEnabled;
        var session = _diagnostics;
        string? reason = System.Threading.Volatile.Read(ref _padObservationFailed) != 0 ? "observer_failed"
            : required && !pad_observation_binding_ready() ? "observer_unready"
            : !OwnsRenderPass || _presentationAdmission.Failure is not null ? "unowned_or_native"
            : !pad_input_presentation_ready() ? "input_group_unready"
            : pad_native_input_owner() ? "native_input_owner"
            : !required && session?.IsCapturing != true ? "capture_inactive" : null;
        if (reason is not null) { _padTimeline.Reset(reason); return false; }
        object owner = required ? this : session!;
        long dropped = required ? 0 : session!.DroppedEvents;
        if (!ReferenceEquals(_padObservationOwner, owner))
        {
            _padObservationOwner = owner;
            _padObservedDropped = dropped;
            _padTimeline.Reset(required ? "observation_owner_changed" : "capture_changed");
            return recording;
        }
        if (!required && dropped != _padObservedDropped)
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
        {
            try { diagnostic_event("pad_observation_stopped", new { error = $"{error.GetType().Name}: {error.Message}" }); }
            catch (Exception) { }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_pad_port_poll(int port, int pad, nint buffer);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_diag_pad_poll(int port, int pad, nint buffer)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_pad_port_poll>(new FhCallLocation(DiagnosticPadPoll, 0)).chain_from(h_diag_pad_poll).fnptr)(port, pad, buffer);
            return;
        }

        try
        {
            new FhCallHandle<d_pad_port_poll>(new FhCallLocation(DiagnosticPadPoll, 0)).chain_from(h_diag_pad_poll).fnptr!(port, pad, buffer);
        }
        catch { reset_pad_observation_timeline("native_poll_exception"); throw; }
        if ((!IndependentPresentationEnabled && _diagnostics?.IsCapturing != true) || System.Threading.Volatile.Read(ref _padObservationFailed) != 0)
        {
            reset_pad_observation_timeline("capture_inactive_or_failed");
            return;
        }
        if (port is < 0 or > 1 || pad != 0)
        {
            reset_pad_observation_timeline("unsupported_pad_identity");
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
            Span<byte> snapshot = stackalloc byte[32];
            long start = Stopwatch.GetTimestamp();
            bool valid = MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100 + 0x80), snapshot);
            long end = Stopwatch.GetTimestamp();
            uint[]? words = valid ? MemoryMarshal.Cast<byte, uint>(snapshot).ToArray() : null;
            lock (_padObservationGate)
            {
                if (eligible && pad_observation_context(false))
                {
                    if (epoch != _padTimeline.Epoch) _padTimeline.Reset("interleaved_observation");
                    else if (words is null) _padTimeline.Reset("invalid_observation");
                    else _padTimeline.Observe(sequence, port, start, end, words);
                }
            }
            try
            {
                if (_diagnostics?.IsCapturing == true)
                    diagnostic_event("pad_observation", new { poll_sequence = sequence, port, pad, stage = "post_port_poll",
                        poll_returned_ticks = returned, observation_start_ticks = start, observation_end_ticks = end,
                        valid, raw_words = words });
            }
            catch (Exception) { /* Telemetry must not stop core observation. */ }
        }
        catch (Exception error) { stop_pad_observations(error); }
    }

    private void pad_observations_prepare_tick(double? deadlineSeconds)
    {
        try
        {
            lock (_padObservationGate)
            {
                _padPreparedTick = null;
                _padPreparedDeadline = null;
                if (!pad_observation_context(false)) return;
                if (!_simulationClock.HasMappedInterval)
                {
                    _padTimeline.Reset("unmapped_clock");
                    return;
                }
                if (deadlineSeconds is null) return;
                _padPreparedTick = _padTimeline.Correlate(deadlineSeconds.Value);
                _padPreparedDeadline = deadlineSeconds;
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
                var result = _padPreparedTick;
                double? preparedDeadline = _padPreparedDeadline;
                _padPreparedTick = null;
                _padPreparedDeadline = null;
                if (!pad_observation_context(false)) return null;
                if (!_render_clock_seen || !_simulationClock.HasMappedInterval)
                {
                    _padTimeline.Reset("unmapped_clock");
                    return null;
                }
                if (deadlineSeconds is null || preparedDeadline != deadlineSeconds
                    || result is null || result.Epoch != _padTimeline.Epoch) return null;
                return new { status = "candidate_only", native_input_applied = false, native_continuity_proven = false,
                    epoch = result.Epoch, boundary = result.Boundary,
                    observations = result.Observations, latest = result.Latest };
            }
        }
        catch (Exception error) { stop_pad_observations(error); return null; }
    }
}