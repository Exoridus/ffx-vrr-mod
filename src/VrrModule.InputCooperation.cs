namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private delegate bool QolBindInputPublication(int version, object owner, Action<long, bool> before, Action after);
    private Action<long, bool>? _qolBeforePublication;
    private Action? _qolAfterPublication;
    [ThreadStatic] private static long _qolCallbackGeneration;
    [ThreadStatic] private static bool _qolCallbackNeutral;
    private long _padNativeClockInvocation, _padNativeClockSequence;
    private int _padNativeClockThread;
    private long _qolPublicationClock, _qolPublicationGeneration;
    private bool _qolPublicationObserved, _qolPublicationActive, _qolPublicationUsed,
        _qolPublicationNeutral, _padPublicationNativeFallback;

    private bool _diagnosticCooperationAttempted, _diagnosticCooperationReported,
        _diagnosticCooperationPadBound, _diagnosticCooperationTicksBound;

    private void init_diagnostic_cooperation(IEnumerable<object> modules)
    {
        if (IndependentPresentationEnabled || _diagnosticCooperationAttempted) return;
        _diagnosticCooperationAttempted = true;
        try
        {
            object[] participants = modules.ToArray();
            _diagnosticCooperationPadBound = bind_pad_input_policy(participants);
            _diagnosticCooperationTicksBound = bind_cooperative_ticks(participants);
        }
        catch (Exception) { }
    }

    private void diagnostic_cooperation_readiness()
    {
        if (!_diagnosticCooperationAttempted || _diagnosticCooperationReported || !RuntimeEffectsArmed) return;
        _diagnosticCooperationReported = true;
        try
        {
            bool qolReady = _diagnosticCooperationPadBound && pad_input_policy(out _, out _);
            bool parryReady = _diagnosticCooperationTicksBound && cooperative_ticks_ready();
            diagnostic_event("cooperation_readiness", new
            {
                pad_policy_bound = _diagnosticCooperationPadBound,
                parry_tick_bound = _diagnosticCooperationTicksBound,
                qol_present = _qolInputPolicy is not null,
                parry_present = _cooperativeTick is not null,
                qol_ready = qolReady,
                parry_ready = parryReady,
                independent_presentation_enabled = IndependentPresentationEnabled,
                simulation_ownership_taken = false
            });
        }
        catch (Exception) { }
    }

    private bool bind_qol_publication(object module)
    {
        try
        {
            var method = module.GetType().GetMethod("TryBindVrrInputPublication",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                [typeof(int), typeof(object), typeof(Action<long, bool>), typeof(Action)]);
            if (method is null || method.ReturnType != typeof(bool)) return false;
            var bind = method.CreateDelegate<QolBindInputPublication>(module);
            _qolBeforePublication ??= pad_qol_before_publication;
            _qolAfterPublication ??= pad_qol_after_publication;
            return bind(1, this, _qolBeforePublication, _qolAfterPublication);
        }
        catch (Exception) { return false; }
    }

    private bool pad_native_clock_owned()
        => RuntimeEffectsArmed && _padInputPolicyBound && _padReplayScope is { Owned: true, Root: true }
            && _padReplayThread == Environment.CurrentManagedThreadId
            && _padReplayScope.Invocation == _padSnapshotInvocation && _padSnapshotLoopDepth == 1
            && _padSnapshotEpoch == RenderPassContext.HistoryEpoch && OwnsRenderPass;

    private void pad_input_cooperation_refuse(string reason)
    {
        _padPublicationNativeFallback = true;
        _qolPublicationActive = _qolPublicationObserved = false;
        _qolPublicationUsed = true;
        _padReplayInvalid = true;
        _padReplayApplied = false;
        _presentationAdmission.Refuse(reason);
        try { pad_replay_cancel(reason); }
        catch (Exception)
        {
            try { reset_pad_observation_timeline(reason); }
            catch (Exception) { }
        }
    }

    private void pad_native_clock_invoke(float delta)
    {
        var original = new FhCallHandle<d_advance_vblank>(new FhCallLocation(EngineAddresses.AdvanceVBlankCounters, 0))
            .chain_from(h_advance_vblank).fnptr!;
        long invocation = 0;
        lock (_padObservationGate)
        {
            if (_padNativeClockInvocation != 0)
                pad_input_cooperation_refuse("pad_native_clock_nested_or_foreign");
            else if (pad_native_clock_owned())
            {
                invocation = System.Threading.Interlocked.Increment(ref _padNativeClockSequence);
                _padNativeClockInvocation = invocation;
                _padNativeClockThread = Environment.CurrentManagedThreadId;
                _qolPublicationClock = _qolPublicationGeneration = 0;
                _qolPublicationObserved = _qolPublicationActive = _qolPublicationUsed = _qolPublicationNeutral = false;
                _padPublicationNativeFallback = false;
            }
            else if (_padReplayLoan is not null)
                pad_input_cooperation_refuse("pad_native_clock_unowned");
        }
        bool normal = false;
        try { original(delta); normal = true; }
        finally
        {
            if (invocation != 0)
            {
                lock (_padObservationGate)
                {
                    try
                    {
                        if (!normal || _qolPublicationActive)
                            pad_input_cooperation_refuse(normal ? "qol_publication_missing_after" : "pad_native_clock_exception");
                    }
                    finally
                    {
                        _padNativeClockInvocation = 0;
                        _padNativeClockThread = 0;
                    }
                }
            }
        }
    }

    private bool pad_qol_expected_publication()
    {
        lock (_padObservationGate)
        {
            if (!pad_native_clock_owned() || _padPublicationNativeFallback || !_qolPublicationObserved
                || !_qolPublicationActive || _qolPublicationUsed || _qolPublicationNeutral
                || _padNativeClockInvocation == 0 || _padNativeClockThread != Environment.CurrentManagedThreadId
                || _qolPublicationClock != _padNativeClockInvocation
                || _padPreparedPolicyGeneration is not { } generation || generation != _qolPublicationGeneration)
                return false;
            _qolPublicationUsed = true;
            return true;
        }
    }

    private void pad_qol_before_publication(long generation, bool neutralize)
    {
        _qolCallbackGeneration = generation;
        _qolCallbackNeutral = neutralize;
        diagnostic_qol_publication("before", generation, neutralize);
        try
        {
            lock (_padObservationGate)
            {
                bool owned = pad_native_clock_owned() && _padNativeClockInvocation != 0
                    && _padNativeClockThread == Environment.CurrentManagedThreadId;
                if (!owned)
                {
                    if (_padReplayLoan is not null)
                    {
                        pad_replay_qol_publication(generation, neutralize);
                        pad_input_cooperation_refuse("qol_publication_unowned");
                    }
                    else if (_padNativeClockInvocation != 0)
                        pad_input_cooperation_refuse("qol_publication_foreign");
                    return;
                }
                if (_qolPublicationObserved || _qolPublicationActive || _qolPublicationUsed)
                {
                    if (neutralize && _padReplayLoan is not null) pad_replay_qol_publication(generation, true);
                    pad_input_cooperation_refuse("qol_publication_duplicate");
                    return;
                }
                _qolPublicationClock = _padNativeClockInvocation;
                _qolPublicationGeneration = generation;
                _qolPublicationNeutral = neutralize;
                _qolPublicationObserved = _qolPublicationActive = true;
                _qolPublicationUsed = false;
                pad_replay_qol_publication(generation, neutralize);
            }
        }
        catch (Exception)
        {
            lock (_padObservationGate) pad_input_cooperation_refuse("qol_publication_before_exception");
        }
    }

    private void pad_qol_after_publication()
    {
        diagnostic_qol_publication("after", _qolCallbackGeneration, _qolCallbackNeutral);
        try
        {
            lock (_padObservationGate)
            {
                if (_padNativeClockInvocation == 0 && _padReplayLoan is null) return;
                if (_padNativeClockInvocation == 0 || !_qolPublicationObserved
                    || !_qolPublicationActive || _padNativeClockThread != Environment.CurrentManagedThreadId
                    || _qolPublicationClock != _padNativeClockInvocation)
                {
                    pad_input_cooperation_refuse("qol_publication_after_unmatched");
                    return;
                }
                _qolPublicationActive = false;
            }
        }
        catch (Exception)
        {
            lock (_padObservationGate) pad_input_cooperation_refuse("qol_publication_after_exception");
        }
    }

    private void diagnostic_qol_publication(string stage, long generation, bool neutralize)
    {
        try
        {
            if (RuntimeEffectsArmed) diagnostic_event("qol_publication", new { stage, generation, neutralize });
        }
        catch (Exception) { }
    }
}
