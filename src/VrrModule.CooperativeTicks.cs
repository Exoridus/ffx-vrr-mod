namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private delegate bool BindParrySimulation(int version, object owner, Func<float, bool> defer,
        out Func<bool>? ready, out Action<long, long, float>? tick);
    private readonly object _cooperativeGate = new();
    private bool _cooperativeBound, _cooperativeFaulted, _cooperativeReadinessChecking,
        _cooperativePending, _cooperativeInside, _cooperativeClock, _cooperativeSimulation,
        _cooperativeDispatched, _cooperativeDispatching;
    private Func<bool>? _cooperativeReady;
    private Action<long, long, float>? _cooperativeTick;
    private Func<float, bool>? _cooperativeDefer;
    private long _cooperativeImage, _cooperativeEpoch, _cooperativeInvocation;
    private int _cooperativeThread;
    private float _cooperativeDelta;
    private PadSnapshotScope _cooperativeRootScope;
    private int _cooperativeRootThread;
    private long _cooperativeRootImage;

    private bool bind_cooperative_ticks(IEnumerable<object> modules)
    {
        if (_cooperativeBound) return false;
        try
        {
            var participants = modules.Where(module => module.GetType().FullName == "Fahrenheit.Mods.Parry.ParryModule").ToArray();
            if (participants.Length > 1) return false;
            Func<bool>? ready = null;
            Action<long, long, float>? tick = null;
            if (participants.Length == 1)
            {
                var method = participants[0].GetType().GetMethod("TryBindVrrSimulation",
                    [typeof(int), typeof(object), typeof(Func<float, bool>), typeof(Func<bool>).MakeByRefType(),
                        typeof(Action<long, long, float>).MakeByRefType()]);
                if (method is null || method.ReturnType != typeof(bool)) return false;
                var bind = method.CreateDelegate<BindParrySimulation>(participants[0]);
                _cooperativeDefer ??= defer_parry_preupdate;
                if (!bind(1, this, _cooperativeDefer, out ready, out tick) || ready is null || tick is null) return false;
            }
            _cooperativeReady = ready;
            _cooperativeTick = tick;
            _cooperativeBound = true;
            return true;
        }
        catch (Exception) { return false; }
    }

    private bool cooperative_ticks_ready()
    {
        Func<bool>? ready;
        lock (_cooperativeGate)
        {
            if (!_cooperativeBound || _cooperativeFaulted) return false;
            ready = _cooperativeReady;
            if (ready is null) return _cooperativeTick is null;
            if (_cooperativeReadinessChecking)
            {
                cooperative_tick_refuse("readiness_reentry");
                return false;
            }
            _cooperativeReadinessChecking = true;
        }
        try
        {
            bool result = ready();
            lock (_cooperativeGate) return result && !_cooperativeFaulted;
        }
        catch (Exception) { cooperative_tick_refuse("readiness_exception"); return false; }
        finally { lock (_cooperativeGate) _cooperativeReadinessChecking = false; }
    }

    private void cooperative_tick_refuse(string reason)
    {
        lock (_cooperativeGate)
        {
            _cooperativeFaulted = true;
            _cooperativePending = false;
        }
        _presentationAdmission.Refuse("cooperative_ticks_" + reason);
    }

    private bool defer_parry_preupdate(float delta)
    {
        if (!float.IsFinite(delta) || delta <= 0 || !cooperative_ticks_ready()) return false;
        try
        {
            if (!RuntimeEffectsArmed || !IndependentPresentationEnabled || !RenderSchedulerReady
                || !_render_frame_enabled || !RenderPassContext.IsActive
                || _render_owner_thread != Environment.CurrentManagedThreadId || !render_scheduler_frame_eligible())
                return false;
            lock (_cooperativeGate)
            {
                if (_cooperativeFaulted || _cooperativeTick is null) return false;
                if (_cooperativePending || _cooperativeInside || _cooperativeDispatching)
                {
                    cooperative_tick_refuse("preupdate_overlap");
                    return false;
                }
                bool root = _cooperativeRootScope is { Owned: true, Root: true }
                    && _cooperativeRootScope.Invocation == _padSnapshotInvocation
                    && _cooperativeRootThread == Environment.CurrentManagedThreadId
                    && _cooperativeRootImage == RenderPassContext.ImageSerial
                    && _padSnapshotOwner == Environment.CurrentManagedThreadId && _padSnapshotLoopDepth == 1
                    && _padSnapshotProducerDepth == 0 && _padSnapshotEpoch == RenderPassContext.HistoryEpoch;
                if (_render_inside_loop && (!root || _render_clock_seen || _padSnapshotClockCompleted))
                {
                    cooperative_tick_refuse("preupdate_phase");
                    return false;
                }
                if (!_render_inside_loop && _cooperativeRootScope.Root)
                {
                    cooperative_tick_refuse("preupdate_root_incomplete");
                    return false;
                }
                if (_render_inside_loop) _cooperativeInside = true;
                _cooperativePending = true;
                _cooperativeImage = RenderPassContext.ImageSerial;
                _cooperativeEpoch = RenderPassContext.HistoryEpoch;
                _cooperativeThread = Environment.CurrentManagedThreadId;
                _cooperativeDelta = delta;
                return true;
            }
        }
        catch (Exception) { cooperative_tick_refuse("preupdate_exception"); return false; }
    }

    private bool cooperative_tick_owner()
        => _cooperativeThread == Environment.CurrentManagedThreadId
            && _cooperativeImage == RenderPassContext.ImageSerial;

    private void cooperative_tick_enter_loop(PadSnapshotScope scope)
    {
        lock (_cooperativeGate)
        {
            if (_cooperativeFaulted || _cooperativeTick is null) return;
            if (!scope.Owned || !scope.Root || _cooperativeRootScope.Root || _cooperativeInside
                || (_cooperativePending && !cooperative_tick_owner()))
            {
                cooperative_tick_refuse("loop_owner");
                return;
            }
            _cooperativeRootScope = scope;
            _cooperativeRootThread = Environment.CurrentManagedThreadId;
            _cooperativeRootImage = RenderPassContext.ImageSerial;
            _cooperativeInvocation = scope.Invocation;
            _cooperativeClock = _cooperativeSimulation = _cooperativeDispatched = false;
            if (_cooperativePending) _cooperativeInside = true;
        }
    }

    private void cooperative_tick_clock_completed(bool successful)
    {
        lock (_cooperativeGate)
        {
            if (!_cooperativePending && !_cooperativeInside) return;
            if (_cooperativeFaulted) return;
            if (!successful || !_cooperativeInside || !cooperative_tick_owner() || _cooperativeClock
                || !_render_clock_seen || !_render_inside_loop || _padSnapshotInvocation != _cooperativeInvocation)
            {
                cooperative_tick_refuse("clock_completion");
                return;
            }
            _cooperativeClock = true;
            _cooperativeSimulation = _render_simulation_pass;
        }
    }

    private void cooperative_tick_snapshot_completed(nuint returnAddress, bool tracked)
    {
        Action<long, long, float>? tick;
        bool nativeHandover;
        float delta;
        long epoch, serial;
        if (!cooperative_ticks_ready())
        {
            lock (_cooperativeGate)
                if (_cooperativePending || _cooperativeInside) cooperative_tick_refuse("participant_lost");
            return;
        }
        nativeHandover = !_render_frame_enabled && (pad_native_input_owner() || _padPublicationNativeFallback);
        bool independent = _render_frame_enabled && IsOwnedRenderLoop && _render_simulation_pass
            && !RenderPassContext.IsPresentation && tracked && _padReplayApplied
            && render_scheduler_frame_eligible();
        lock (_cooperativeGate)
        {
            if (!_cooperativePending && !_cooperativeInside) return;
            if (_cooperativeFaulted) return;
            if (!_cooperativeInside || !cooperative_tick_owner() || _cooperativeDispatching || _cooperativeDispatched
                || !_cooperativeClock || !_cooperativeSimulation || !_render_inside_loop
                || _padSnapshotInvocation != _cooperativeInvocation || _padSnapshotLoopDepth != 1
                || returnAddress != (nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation))
            {
                cooperative_tick_refuse("snapshot_identity");
                return;
            }
            independent &= _cooperativeEpoch == RenderPassContext.HistoryEpoch;
            if (!nativeHandover && !independent)
            {
                cooperative_tick_refuse("snapshot_unqualified");
                return;
            }
            delta = nativeHandover && !_padReplayApplied ? _cooperativeDelta : 1f / TargetFramerate;
            epoch = RenderPassContext.HistoryEpoch;
            serial = RenderPassContext.SimulationSerial;
            tick = _cooperativeTick;
            _cooperativePending = false;
            _cooperativeDispatched = _cooperativeDispatching = true;
        }
        try { tick!(epoch, serial, delta); }
        catch (Exception) { cooperative_tick_refuse("dispatch_exception"); throw; }
        finally { lock (_cooperativeGate) _cooperativeDispatching = false; }
        // Consumer execution is committed before optional telemetry.
        try { diagnostic_event("cooperative_simulation_tick", new { epoch, tick = serial, native_handover = nativeHandover }); }
        catch (Exception) { }
    }

    private void cooperative_tick_exit_loop(PadSnapshotScope scope)
    {
        lock (_cooperativeGate)
        {
            try
            {
                if (!_cooperativePending && !_cooperativeInside) return;
                if (!_cooperativeFaulted && (!scope.Owned || !scope.Root || scope.Invocation != _cooperativeInvocation
                    || !cooperative_tick_owner() || !_cooperativeInside || !_cooperativeClock
                    || (_cooperativeSimulation && !_cooperativeDispatched) || _cooperativeDispatching))
                    cooperative_tick_refuse("incomplete_loop");
                _cooperativePending = _cooperativeInside = false;
            }
            finally
            {
                if (scope.Root && scope.Invocation == _cooperativeRootScope.Invocation
                    && Environment.CurrentManagedThreadId == _cooperativeRootThread)
                    _cooperativeRootScope = default;
            }
        }
    }
}
