namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private delegate bool QolInputPolicy(int version, out long generation, out bool neutralize);
    private QolInputPolicy? _qolInputPolicy;
    private PadTickCorrelation? _padReplayRequested;
    private long _padReplayRequestedGeneration;
    private long? _padPreparedPolicyGeneration;
    private PadObservedSample?[] _padPreparedObservations = [];
    private long _padPreparedPollSequence;
    private bool _padInputPolicyBound, _padReplayInvalid, _padReplayRestoreAttempted, _padReplayApplied;
    private PadSnapshotScope _padReplayScope;
    private int _padReplayThread;
    private PadReplayLoan? _padReplayLoan;
    private sealed record PadReplayLoan(long Invocation, long Clock, PadTickCorrelation Tick, long PolicyGeneration,
        long PollSequence, uint[][] Native, uint[][] Selected, int[] WrittenMasks);

    private bool bind_pad_input_policy(IEnumerable<object> modules)
    {
        if (_padInputPolicyBound) return false;
        try
        {
            var owners = modules.Where(module => module.GetType().FullName == "Fahrenheit.Mods.Qol.FocusInputModule").ToArray();
            if (owners.Length > 1) return false;
            QolInputPolicy? policy = null;
            if (owners.Length == 1)
            {
                var method = owners[0].GetType().GetMethod("TryGetVrrInputPolicy",
                    [typeof(int), typeof(long).MakeByRefType(), typeof(bool).MakeByRefType()]);
                if (method is null || method.ReturnType != typeof(bool)) return false;
                policy = method.CreateDelegate<QolInputPolicy>(owners[0]);
                if (!bind_qol_publication(owners[0])) return false;
            }
            _qolInputPolicy = policy;
            _padInputPolicyBound = true;
            return true;
        }
        catch (Exception) { return false; }
    }

    private bool pad_input_policy(out long generation, out bool neutralize)
    {
        generation = 0;
        neutralize = false;
        if (!_padInputPolicyBound) return false;
        try { return _qolInputPolicy is null || _qolInputPolicy(1, out generation, out neutralize); }
        catch (Exception) { return false; }
    }

    private bool pad_qol_native_owner()
    {
        if (_qolPublicationObserved && (_padNativeClockInvocation != 0 && _qolPublicationClock == _padNativeClockInvocation
            || _padReplayLoan is { } loan && loan.Clock == _qolPublicationClock))
            return _qolPublicationNeutral;
        return _padInputPolicyBound && (!pad_input_policy(out _, out bool neutralize) || neutralize);
    }

    private void pad_replay_enter_loop(PadSnapshotScope scope)
    {
        lock (_padObservationGate)
        {
            if (!scope.Owned || !scope.Root)
            {
                if (_padReplayLoan is not null) pad_replay_cancel("pad_replay_nested_or_foreign_loop");
                return;
            }
            if (_padReplayLoan is not null) { _padReplayInvalid = true; return; }
            _padReplayScope = scope;
            _padReplayThread = Environment.CurrentManagedThreadId;
            _padReplayInvalid = false;
        }
    }

    private bool pad_replay_begin(PadTickCorrelation tick)
    {
        if (_qolInputPolicy is not null)
        {
            if (_padPreparedPolicyGeneration is not { } generation) return false;
            if (_qolPublicationObserved && _qolPublicationClock == _padNativeClockInvocation)
            {
                if (_qolPublicationNeutral || generation != _qolPublicationGeneration)
                {
                    pad_replay_native_fallback("qol_policy_changed_at_publication");
                    return true;
                }
                return pad_replay_stage(tick, generation);
            }
            _padReplayRequested = tick;
            _padReplayRequestedGeneration = generation;
            return true;
        }
        return pad_replay_stage(tick, 0);
    }

    private void pad_replay_native_fallback(string reason)
    {
        _padPublicationNativeFallback = true;
        _padReplayRequested = null;
        _padReplayInvalid = true;
        _padReplayApplied = false;
        pad_replay_restore();
        reset_pad_observation_timeline(reason);
    }

    private void pad_replay_qol_publication(long generation, bool neutralize)
    {
        if (neutralize)
        {
            // QoL wrote every payload word, including words equal to our selected value.
            _padReplayLoan = null;
            if (_padReplayRequested is not null || _padPreparedTick is not null)
                pad_replay_native_fallback("qol_neutralized_publication");
            return;
        }
        var tick = _padReplayRequested;
        if (tick is null) return;
        _padReplayRequested = null;
        if (_padReplayRequestedGeneration != generation || !ReferenceEquals(tick, _padPreparedTick))
        {
            pad_replay_native_fallback("qol_policy_changed_at_publication");
            return;
        }
        if (!pad_replay_stage(tick, generation)) pad_input_cooperation_refuse("qol_publication_staging_failed");
    }

    private bool pad_replay_stage(PadTickCorrelation tick, long generation)
    {
        if (!_padInputPolicyBound || !_padReplayScope.Root || _padReplayInvalid || _padReplayLoan is not null
            || _padReplayThread != Environment.CurrentManagedThreadId || _padReplayScope.Invocation != _padSnapshotInvocation
            || _padSnapshotLoopDepth != 1 || _padSnapshotEpoch != RenderPassContext.HistoryEpoch
            || System.Threading.Volatile.Read(ref _padPublicationDepth) != 1)
            return false;
        if (_padNativeClockInvocation == 0 || _padNativeClockThread != Environment.CurrentManagedThreadId) return false;
        if (_padPreparedObservations.Length != 2
            || System.Threading.Interlocked.Read(ref _padObservationSequence) != _padPreparedPollSequence
            || pad_publication_check(tick, _padPreparedObservations) is not null) return false;
        uint[][] native = new uint[2][], selected = new uint[2][];
        Span<byte> raw = stackalloc byte[32];
        var observed = _padPreparedObservations;
        for (int port = 0; port < 2; port++)
        {
            if (tick.Latest[port] is not { } sample || observed[port] is not { } latest
                || !MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100 + 0x80), raw))
                return false;
            native[port] = MemoryMarshal.Cast<byte, uint>(raw).ToArray();
            selected[port] = (uint[])sample.RawWords.Clone();
            if (!pad_replay_metadata_equal(native[port], latest.RawWords)
                || !pad_replay_payload_equal(native[port], latest.RawWords)) return false;
        }
        if (System.Threading.Interlocked.Read(ref _padObservationSequence) != _padPreparedPollSequence
            || pad_publication_check(tick, observed) is not null) return false;
        var loan = new PadReplayLoan(_padReplayScope.Invocation, _padNativeClockInvocation, tick, generation,
            System.Threading.Interlocked.Read(ref _padObservationSequence), native, selected, new int[2]);
        _padReplayLoan = loan;
        _padReplayRestoreAttempted = false;
        for (int port = 0; port < 2; port++)
        {
            uint* target = FhUtil.ptr_at<uint>(PadRecords + port * 0x100 + 0x80);
            for (int i = 0; i < 5; i++)
            {
                int word = i < 4 ? i + 1 : 6;
                int previous = System.Threading.Interlocked.CompareExchange(ref *(int*)(target + word),
                    unchecked((int)selected[port][word]), unchecked((int)native[port][word]));
                if (unchecked((uint)previous) != native[port][word]) return false;
                loan.WrittenMasks[port] |= 1 << i;
            }
        }
        return true;
    }

    private static bool pad_replay_payload_equal(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
        => left.Slice(1, 4).SequenceEqual(right.Slice(1, 4)) && left[6] == right[6];

    private static bool pad_replay_metadata_equal(ReadOnlySpan<uint> left, ReadOnlySpan<uint> right)
        => left[0] == right[0] && left[5] == right[5] && (left[7] & 0xffff0000u) == (right[7] & 0xffff0000u);

    private bool pad_replay_policy_unchanged()
        => _padReplayLoan is not { } loan || !_padReplayInvalid
            && (_qolInputPolicy is null || _qolPublicationObserved && !_qolPublicationNeutral
                && _qolPublicationClock == loan.Clock && _qolPublicationGeneration == loan.PolicyGeneration)
            && System.Threading.Interlocked.Read(ref _padObservationSequence) == loan.PollSequence;

    private void pad_replay_cancel(string reason)
    {
        _padReplayInvalid = true;
        _padReplayApplied = false;
        _presentationAdmission.Refuse(reason);
        pad_replay_restore();
        reset_pad_observation_timeline(reason);
    }

    private void pad_replay_restore()
    {
        if (_padReplayLoan is not { } loan || _padReplayRestoreAttempted) return;
        if (_padReplayThread != Environment.CurrentManagedThreadId)
        {
            _presentationAdmission.Refuse("pad_replay_restore_owner_changed");
            return;
        }
        _padReplayRestoreAttempted = true;
        if (System.Threading.Interlocked.Read(ref _padObservationSequence) != loan.PollSequence)
        {
            _presentationAdmission.Refuse("pad_replay_restore_poll_changed");
            return;
        }
        bool restored = true;
        Span<byte> raw = stackalloc byte[32];
        for (int port = 0; port < 2; port++)
        {
            try
            {
                if (!MemoryProbe.TryCopy((nint)FhUtil.ptr_at<byte>(PadRecords + port * 0x100 + 0x80), raw)
                    || !pad_replay_metadata_equal(MemoryMarshal.Cast<byte, uint>(raw), loan.Native[port]))
                {
                    restored = false;
                    continue;
                }
                uint* target = FhUtil.ptr_at<uint>(PadRecords + port * 0x100 + 0x80);
                for (int i = 0; i < 5; i++)
                {
                    if ((loan.WrittenMasks[port] & (1 << i)) == 0) continue;
                    int word = i < 4 ? i + 1 : 6;
                    int previous = System.Threading.Interlocked.CompareExchange(ref *(int*)(target + word),
                        unchecked((int)loan.Native[port][word]), unchecked((int)loan.Selected[port][word]));
                    if (unchecked((uint)previous) == loan.Selected[port][word]) loan.WrittenMasks[port] &= ~(1 << i);
                    else restored = false;
                }
            }
            catch (Exception) { restored = false; }
        }
        if (restored) _padReplayLoan = null;
        else
        {
            _presentationAdmission.Refuse("pad_replay_restore_conflict");
            reset_pad_observation_timeline("pad_replay_restore_conflict");
        }
    }

    private void pad_replay_before_snapshot()
    {
        lock (_padObservationGate)
        {
            if (_padReplayLoan is not null && (_padReplayInvalid || !pad_replay_policy_unchanged()
                || _padSnapshotLoopDepth != 1))
                pad_replay_cancel("pad_replay_snapshot_owner_changed");
        }
    }

    private void pad_replay_exit_loop(PadSnapshotScope scope)
    {
        if (!scope.Owned || !scope.Root) return;
        lock (_padObservationGate)
        {
            if (_padReplayThread != Environment.CurrentManagedThreadId || scope.Invocation != _padReplayScope.Invocation) return;
            try { pad_replay_restore(); }
            finally { _padReplayScope = default; }
        }
    }
}
