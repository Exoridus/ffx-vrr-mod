namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Telemetry for slow scene transitions: VirtualQuery traffic from MemoryProbe, main loop passes
///     and the longest frame, so a frame-rate drop can be attributed to this module or to the engine.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private long _load_frame_last, _load_frame_max, _load_passes;

    // Transition summary: a run of frames over 30 ms, closed after one second without one.
    private long _transition_start, _transition_last_long, _transition_longest, _transition_long_frames, _transition_sim_start;
    private Dictionary<uint, (long Wait, long Hold, long Entries)>? _transition_locks;

    // Present cost split into module pacing and the native call, and how much of it ran under the device lock.
    private long _present_pace_ticks, _present_call_ticks, _present_locked_ticks, _present_count, _present_locked_count, _present_released_count;
    private (long Pace, long Call, long Locked, long Count, long LockedCount, long Released) _transition_present;

    private void present_telemetry(long pace, long call, bool locked, bool released)
    {
        System.Threading.Interlocked.Add(ref _present_pace_ticks, pace);
        System.Threading.Interlocked.Add(ref _present_call_ticks, call);
        System.Threading.Interlocked.Increment(ref _present_count);
        if (released) System.Threading.Interlocked.Increment(ref _present_released_count);
        if (!locked) return;
        System.Threading.Interlocked.Add(ref _present_locked_ticks, pace + call);
        System.Threading.Interlocked.Increment(ref _present_locked_count);
    }

    private (long Pace, long Call, long Locked, long Count, long LockedCount, long Released) present_snapshot()
        => (System.Threading.Interlocked.Read(ref _present_pace_ticks), System.Threading.Interlocked.Read(ref _present_call_ticks),
            System.Threading.Interlocked.Read(ref _present_locked_ticks), System.Threading.Interlocked.Read(ref _present_count),
            System.Threading.Interlocked.Read(ref _present_locked_count), System.Threading.Interlocked.Read(ref _present_released_count));

    private void load_telemetry_frame()
    {
        long now = Stopwatch.GetTimestamp();
        long frame = _load_frame_last == 0 ? 0 : now - _load_frame_last;
        if (_load_frame_last != 0) _load_frame_max = Math.Max(_load_frame_max, frame);
        _load_frame_last = now;
        long f = Stopwatch.Frequency;
        if (frame > f * 30 / 1000)
        {
            if (_transition_start == 0)
            {
                _transition_start = now - frame;
                _transition_longest = _transition_long_frames = 0;
                _transition_sim_start = _simulation_ticks;
                _transition_locks = device_lock_snapshot();
                _transition_present = present_snapshot();
            }
            _transition_last_long = now;
            _transition_long_frames++;
            _transition_longest = Math.Max(_transition_longest, frame);
            return;
        }
        if (_transition_start == 0 || now - _transition_last_long < f) return;
        double ms = 1000.0 / f;
        var before = _transition_locks ?? new();
        var p = present_snapshot();
        var locks = device_lock_snapshot()
            .Select(kv => (kv.Key, d: kv.Value, b: before.GetValueOrDefault(kv.Key)))
            .Select(x => (x.Key, Wait: (x.d.Wait - x.b.Wait) * ms, Hold: (x.d.Hold - x.b.Hold) * ms, Entries: x.d.Entries - x.b.Entries))
            .Where(x => x.Entries > 0).OrderByDescending(x => x.Hold + x.Wait).Take(5)
            .Select(x => $"tid {x.Key}: hold {x.Hold:F0}ms wait {x.Wait:F0}ms n={x.Entries}");
        _logger.Info($"[Vrr] transition: {(_transition_last_long - _transition_start) * ms:F0}ms, {_transition_long_frames} frames >30ms, " +
                     $"longest {_transition_longest * ms:F0}ms, sim_ticks +{_simulation_ticks - _transition_sim_start}, " +
                     $"rate cap {PresentationTargetHz:F0}; device lock: {string.Join("; ", locks)}; " +
                     $"present: n={p.Count - _transition_present.Count} under_lock={p.LockedCount - _transition_present.LockedCount} released={p.Released - _transition_present.Released} " +
                     $"pace {(p.Pace - _transition_present.Pace) * ms:F0}ms native {(p.Call - _transition_present.Call) * ms:F0}ms " +
                     $"locked {(p.Locked - _transition_present.Locked) * ms:F0}ms");
        _transition_start = 0;
    }

    private string load_telemetry_counts()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        string line = $"load=vq:{System.Threading.Interlocked.Read(ref MemoryProbe.QueryCalls)}(miss {System.Threading.Interlocked.Read(ref MemoryProbe.QueryMisses)})/{System.Threading.Interlocked.Read(ref MemoryProbe.QueryTicks) * ms:F0}ms " +
                      $"passes:{_load_passes} maxframe:{_load_frame_max * ms:F1}ms";
        _load_frame_max = 0;
        return line;
    }
}
