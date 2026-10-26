namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint PadSnapshotProducer = 0x42d070, PadSnapshotCall = 0x420df0,
        PadSnapshotContinuation = 0x420df5;
    private const nint PadSnapshotNamesReset = 0xd33350, PadSnapshotPress = 0x1fc44a8,
        PadSnapshotDebug = 0xf3c910, PadSnapshotLr = 0xf007b0, PadSnapshotSelector = 0xf007b1,
        PadSnapshotByteLatch = 0xf007b3, PadSnapshotDwordLatch = 0x1fc44b4;
    private readonly object _padSnapshotGate = new();
    private d_render_void? _padSnapshotEntry;
    private NativeSnapshotBridge.Dispatch? _padSnapshotDispatch;
    private bool _padSnapshotInstalled, _padSnapshotBaseline;
    private long _padSnapshotBaselineEpoch, _padSnapshotInvocation, _padSnapshotEpoch;
    private int _padSnapshotOwner, _padSnapshotLoopDepth, _padSnapshotProducerDepth;
    private bool _padSnapshotInvalid, _padSnapshotClockCompleted, _padSnapshotSimulation,
        _padSnapshotReached, _padSnapshotReturned, _padSnapshotHeld, _padSnapshotExecPositive;
    private readonly record struct PadSnapshotScope(long Invocation, bool Owned, bool Root);

    private bool prepare_pad_snapshot_hook()
    {
        if (!pad_snapshot_binding_ready()) return false;
        _padSnapshotDispatch = h_pad_snapshot_dispatch;
        return NativeSnapshotBridge.TryCreate(_padSnapshotDispatch, out _padSnapshotEntry);
    }

    private bool init_pad_snapshot_hook()
    {
        if (!IndependentPresentationEnabled) return true;
        if (_padSnapshotEntry is null) return false;
        _padSnapshotInstalled = hook_or_log(HookSiteId.PadSnapshotProducer, "pad snapshot producer", PadSnapshotProducer,
            () => new FhCallHandle<d_render_void>(new FhCallLocation(PadSnapshotProducer, 0)).hook(this, _padSnapshotEntry!));
        return _padSnapshotInstalled;
    }

    private bool pad_snapshot_binding_ready()
        => fx_code_matches(PadSnapshotCall, "E8 7B C2 00 00") && fx_call_is(PadSnapshotCall, PadSnapshotProducer)
            && pad_snapshot_read<uint>(PadSnapshotNamesReset, out _)
            && pad_snapshot_read<uint>(PadSnapshotPress, out _)
            && pad_snapshot_read<byte>(PadSnapshotDebug, out _) && pad_snapshot_read<byte>(PadSnapshotLr, out _)
            && pad_snapshot_read<byte>(PadSnapshotSelector, out _) && pad_snapshot_read<byte>(PadSnapshotByteLatch, out _)
            && pad_snapshot_read<uint>(PadSnapshotDwordLatch, out _);

    private static bool pad_snapshot_read<T>(nint rva, out T value) where T : unmanaged
    {
        T* at = FhUtil.ptr_at<T>(rva);
        value = default;
        if (!MemoryProbe.IsReadable((nint)at, sizeof(T))) return false;
        value = *at;
        return true;
    }

    private bool pad_snapshot_guards()
        => pad_snapshot_binding_ready() && pad_input_presentation_ready() && !pad_native_input_owner()
            && _presentationAdmission.Failure is null
            && pad_snapshot_read<uint>(PadSnapshotNamesReset, out uint names) && names == 0
            && pad_snapshot_read<uint>(PadSnapshotPress, out uint press) && (press & 0x10) == 0
            && pad_snapshot_read<byte>(PadSnapshotDebug, out byte debug) && debug == 0
            && pad_snapshot_read<byte>(PadSnapshotLr, out byte lr) && lr == 0
            && pad_snapshot_read<byte>(PadSnapshotSelector, out byte selector) && selector == 0
            && pad_snapshot_read<byte>(PadSnapshotByteLatch, out byte byteLatch) && byteLatch == 0
            && pad_snapshot_read<uint>(PadSnapshotDwordLatch, out uint dwordLatch) && dwordLatch == 0;

    private bool pad_snapshot_presentation_ready()
    {
        lock (_padSnapshotGate)
            return _padSnapshotInstalled && _padSnapshotBaseline
                && _padSnapshotBaselineEpoch == RenderPassContext.HistoryEpoch && pad_snapshot_guards();
    }

    private PadSnapshotScope pad_snapshot_enter_loop()
    {
        lock (_padSnapshotGate)
        {
            int thread = Environment.CurrentManagedThreadId;
            if (_padSnapshotOwner != 0 && _padSnapshotOwner != thread) return default;
            if (_padSnapshotOwner == thread)
            {
                _padSnapshotLoopDepth++;
                pad_snapshot_invalidate();
                return new(_padSnapshotInvocation, true, false);
            }
            _padSnapshotOwner = thread;
            _padSnapshotInvocation++;
            _padSnapshotLoopDepth = 1;
            _padSnapshotProducerDepth = 0;
            _padSnapshotEpoch = RenderPassContext.HistoryEpoch;
            _padSnapshotInvalid = _padSnapshotClockCompleted = _padSnapshotReached = _padSnapshotReturned = _padSnapshotHeld = false;
            _padSnapshotExecPositive = false;
            if (_padSnapshotBaselineEpoch != _padSnapshotEpoch) _padSnapshotBaseline = false;
            return new(_padSnapshotInvocation, true, true);
        }
    }

    private void pad_snapshot_invalidate()
    {
        _padSnapshotInvalid = true;
        _padSnapshotBaseline = false;
        if (_padSnapshotHeld) _presentationAdmission.Refuse("pad_snapshot_invalidated_after_hold");
    }

    private void pad_snapshot_clock_completed(bool successful)
    {
        lock (_padSnapshotGate)
        {
            if (_padSnapshotOwner != Environment.CurrentManagedThreadId || _padSnapshotLoopDepth == 0) return;
            if (!successful || _padSnapshotClockCompleted || _padSnapshotLoopDepth != 1
                || !IsOwnedRenderLoop || !_render_clock_seen || _padSnapshotEpoch != RenderPassContext.HistoryEpoch)
            {
                pad_snapshot_invalidate();
                return;
            }
            _padSnapshotClockCompleted = true;
            _padSnapshotSimulation = _render_simulation_pass;
        }
    }

    private void pad_snapshot_exit_loop(PadSnapshotScope scope, bool successful)
    {
        if (!scope.Owned) return;
        lock (_padSnapshotGate)
        {
            if (_padSnapshotOwner != Environment.CurrentManagedThreadId || scope.Invocation != _padSnapshotInvocation) return;
            try
            {
                if (!successful || _padSnapshotEpoch != RenderPassContext.HistoryEpoch) pad_snapshot_invalidate();
                if (!successful && _padSnapshotInstalled) _presentationAdmission.Refuse("pad_snapshot_loop_failure");
                if (!scope.Root) return;
                if (!_padSnapshotInstalled) { _padSnapshotBaseline = false; return; }
                if (!_padSnapshotInvalid && (!RenderSchedulerReady || !render_scheduler_frame_eligible() || !pad_snapshot_guards()))
                    pad_snapshot_invalidate();
                if (!_padSnapshotInvalid && _padSnapshotLoopDepth == 1 && _padSnapshotClockCompleted
                    && _padSnapshotReached && _padSnapshotReturned && _padSnapshotSimulation && _padSnapshotExecPositive)
                {
                    _padSnapshotBaseline = true;
                    _padSnapshotBaselineEpoch = _padSnapshotEpoch;
                    if (_render_snapshot_warmup)
                    {
                        // The initial plan accounts for the native tick that just completed.
                        _simulationClock.Reset();
                        _simulationClock.Plan(_render_now, _render_clock_speed);
                    }
                }
                else if (!_padSnapshotHeld) _padSnapshotBaseline = false;
            }
            finally
            {
                if (scope.Root)
                {
                    _padSnapshotOwner = _padSnapshotLoopDepth = 0;
                    _padSnapshotHeld = false;
                }
                else _padSnapshotLoopDepth--;
            }
        }
    }

    private bool pad_snapshot_exec_must_stay_zero()
    {
        lock (_padSnapshotGate)
        {
            if (_padSnapshotOwner != Environment.CurrentManagedThreadId || !_padSnapshotHeld) return false;
            try
            {
                if (_padSnapshotEpoch != RenderPassContext.HistoryEpoch || !IsOwnedPresentationPass
                    || !pad_snapshot_guards()) pad_snapshot_invalidate();
            }
            catch { pad_snapshot_invalidate(); }
            return true;
        }
    }

    private void pad_snapshot_exec_completed(uint effective)
    {
        lock (_padSnapshotGate)
        {
            if (_padSnapshotOwner == Environment.CurrentManagedThreadId && _padSnapshotLoopDepth == 1
                && _padSnapshotClockCompleted && _padSnapshotSimulation && _padSnapshotReturned && !RenderPassContext.IsPresentation
                && effective > 0) _padSnapshotExecPositive = true;
        }
    }

    private void h_pad_snapshot_dispatch(nuint returnAddress, nuint returnSlot)
    {
        d_render_void original;
        try
        {
            original = new FhCallHandle<d_render_void>(new FhCallLocation(PadSnapshotProducer, 0))
                .chain_from(_padSnapshotEntry!).fnptr!;
            if (original is null) throw new InvalidOperationException("Snapshot next chain is unavailable.");
        }
        catch (Exception error)
        {
            Environment.FailFast("Snapshot next-chain resolution failed.", error);
            return;
        }
        try { pad_snapshot_invoke(returnAddress, original); }
        catch (Exception error) { Environment.FailFast("Snapshot native invocation or completion failed; forwarding cannot be retried.", error); }
    }

    private void pad_snapshot_invoke(nuint returnAddress, d_render_void original)
    {
        bool owner = false, tracked = false;
        lock (_padSnapshotGate)
        {
            owner = _padSnapshotOwner == Environment.CurrentManagedThreadId && _padSnapshotLoopDepth != 0;
            if (!owner)
            {
                pad_snapshot_invalidate();
                _presentationAdmission.Refuse("pad_snapshot_unowned_producer");
            }
            if (owner)
            {
                _padSnapshotProducerDepth++;
                try
                {
                    bool identity = returnAddress == (nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation);
                    bool ready = _padSnapshotInstalled && identity && _padSnapshotLoopDepth == 1
                        && _padSnapshotProducerDepth == 1 && !_padSnapshotReached && _padSnapshotClockCompleted
                        && RenderPassContext.IsPresentation == !_padSnapshotSimulation
                        && _padSnapshotEpoch == RenderPassContext.HistoryEpoch && IsOwnedRenderLoop && pad_snapshot_guards();
                    if (!ready) pad_snapshot_invalidate();
                    tracked = ready && !_padSnapshotInvalid;
                    if (tracked) _padSnapshotReached = true;
                    if (tracked && !_padSnapshotSimulation && IsOwnedPresentationPass && _padSnapshotBaseline
                        && _padSnapshotBaselineEpoch == _padSnapshotEpoch)
                    {
                        _padSnapshotHeld = true;
                        _padSnapshotProducerDepth--;
                        return;
                    }
                }
                catch
                {
                    pad_snapshot_invalidate();
                    _presentationAdmission.Refuse("pad_snapshot_policy_failure");
                    tracked = false;
                }
                if (RenderPassContext.IsPresentation || (_padSnapshotClockCompleted && !_padSnapshotSimulation))
                    _presentationAdmission.Refuse("pad_snapshot_presentation_forwarded");
            }
        }
        bool returned = false;
        try { original(); returned = true; }
        finally
        {
            if (owner)
            {
                lock (_padSnapshotGate)
                {
                    _padSnapshotProducerDepth--;
                    if (!returned || _padSnapshotEpoch != RenderPassContext.HistoryEpoch)
                        pad_snapshot_invalidate();
                    else if (!_padSnapshotInvalid)
                    {
                        if (!pad_snapshot_guards()) pad_snapshot_invalidate();
                        else if (tracked) _padSnapshotReturned = true;
                    }
                }
            }
            else
            {
                lock (_padSnapshotGate)
                {
                    pad_snapshot_invalidate();
                    _presentationAdmission.Refuse("pad_snapshot_unowned_producer");
                }
            }
        }
    }
}
