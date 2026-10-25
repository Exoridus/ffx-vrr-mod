using System.Threading;

namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Phyre's global device lock (RVA 0x894EE4, a recursive PCriticalSectionBase: +0xC waiters,
///     +0x10 owner thread id, +0x14 recursion) is not fair. Release signals its event when someone
///     waits, but the releasing thread takes the lock straight back with a compare-exchange and resets
///     the event on success, so the woken waiter finds it taken and sleeps out its 20 ms timeout again.
///     PRenderThread::processRenderCommands holds the lock for a whole frame's command list, so at
///     144 Hz the render thread releases it only for an instant between frames and the scene loader's
///     resource jobs starve: transitions took seconds longer than at 60 Hz, and longer the higher the
///     frame rate. Entering the lock while it is free and another thread waits now gives the waiter up
///     to a millisecond to take it first, which makes loading independent of the frame rate.
/// </summary>
public unsafe sealed partial class VrrModule
{
    // FUN_00463e10 in the v3 decompilation: the scoped-lock constructor every one of its 82 callers uses.
    private const nint DeviceLockEnter = 0x63D50, DeviceLockLeave = 0x63E90, DeviceLock = 0x894EE4;
    private const string DeviceLockEnterPattern =
        "55 8B EC 6A FF 68 ?? ?? ?? ?? 64 A1 00 00 00 00 50 51 56 A1 ?? ?? ?? ?? 33 C5 50 8D 45 F4 64 A3 00 00 00 00 8B F1 89 75 F0 A1 ?? ?? ?? ?? A8 01 75 2D 83 C8 01 A3 ?? ?? ?? ?? B9";
    private long _device_lock_yields, _device_lock_handoffs, _device_lock_yield_ticks;

    // Measurement: per thread, ticks spent waiting to enter and holding the lock (outermost level only).
    private sealed class LockStats { public long Wait, Hold, Entries; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, LockStats> _device_lock_stats = new();
    [ThreadStatic] private static long _device_lock_held_since;

    private string device_lock_counts()
        => $"devlock=yield:{_device_lock_yields}/handoff:{_device_lock_handoffs}/{_device_lock_yield_ticks * 1000.0 / Stopwatch.Frequency:F0}ms";

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void d_device_lock_enter(nint guard);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern bool SwitchToThread();

    private bool init_device_lock_fairness()
    {
        if (!_config.VrrEnabled) return true;
        if (!fx_code_matches(DeviceLockEnter, DeviceLockEnterPattern) || !fx_operand_is(DeviceLockEnter + 0x3B, DeviceLock))
        {
            _logger.Error("[Vrr] Phyre device lock bytes differ from build 25501027; loading stays frame-rate bound.");
            return true;
        }
        if (!fx_code_matches(DeviceLockLeave, "55 8B EC 6A FF 68") || !fx_operand_is(DeviceLockLeave + 0x42, DeviceLock))
        {
            _logger.Error("[Vrr] Phyre device lock release bytes differ from build 25501027; lock fairness not installed.");
            return true;
        }
        _device_lock_verified = true;
        return hook_or_log("Phyre device lock fairness", DeviceLockEnter,
                () => new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockEnter, 0)).hook(this, h_device_lock_enter))
            && hook_or_log("Phyre device lock release", DeviceLockLeave,
                () => new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockLeave, 0)).hook(this, h_device_lock_leave));
    }

    private bool _device_lock_verified;
    [ThreadStatic] private static bool _device_lock_no_yield;

    // True when this thread holds the lock exactly once, so one release frees it completely.
    private bool device_lock_owned_once()
    {
        if (!_device_lock_verified) return false;
        int* lockState = FhUtil.ptr_at<int>(DeviceLock);
        return (uint)Volatile.Read(ref lockState[4]) == GetCurrentThreadId() && lockState[5] == 1;
    }

    // The guard holds the lock address, exactly as the scoped-lock constructor leaves it.
    private bool device_lock_release()
    {
        nint guard = (nint)FhUtil.ptr_at<byte>(DeviceLock);
        ((delegate* unmanaged[Thiscall]<nint*, void>)FhUtil.ptr_at<byte>(DeviceLockLeave))(&guard);
        return true;
    }

    private void device_lock_reacquire()
    {
        nint guard = (nint)FhUtil.ptr_at<byte>(DeviceLock);
        // The render thread must not hand the lock it is about to resume with to a loader.
        _device_lock_no_yield = true;
        try { ((delegate* unmanaged[Thiscall]<nint*, void>)FhUtil.ptr_at<byte>(DeviceLockEnter))(&guard); }
        finally { _device_lock_no_yield = false; }
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private void h_device_lock_leave(nint guard)
    {
        int* lockState = FhUtil.ptr_at<int>(DeviceLock);
        if (lockState[5] == 1 && _device_lock_held_since != 0)
        {
            var stats = _device_lock_stats.GetOrAdd(GetCurrentThreadId(), _ => new LockStats());
            Interlocked.Add(ref stats.Hold, Stopwatch.GetTimestamp() - _device_lock_held_since);
            _device_lock_held_since = 0;
        }
        new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockLeave, 0)).chain_from(h_device_lock_leave).fnptr!(guard);
    }

    // Snapshot of the per-thread totals, for the transition summary.
    private Dictionary<uint, (long Wait, long Hold, long Entries)> device_lock_snapshot()
        => _device_lock_stats.ToDictionary(kv => kv.Key,
            kv => (Interlocked.Read(ref kv.Value.Wait), Interlocked.Read(ref kv.Value.Hold), Interlocked.Read(ref kv.Value.Entries)));

    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private void h_device_lock_enter(nint guard)
    {
        int* lockState = FhUtil.ptr_at<int>(DeviceLock);
        if (!_device_lock_no_yield && Volatile.Read(ref lockState[3]) > 0 && Volatile.Read(ref lockState[4]) == 0)
        {
            uint self = GetCurrentThreadId();
            long start = Stopwatch.GetTimestamp(), limit = start + Stopwatch.Frequency / 1000;
            Interlocked.Increment(ref _device_lock_yields);
            // Free with a waiter: the waiter was signalled on release and only needs the CPU to take it.
            while (Volatile.Read(ref lockState[3]) > 0 && Volatile.Read(ref lockState[4]) == 0 && Stopwatch.GetTimestamp() < limit)
                SwitchToThread();
            uint owner = (uint)Volatile.Read(ref lockState[4]);
            if (owner != 0 && owner != self) Interlocked.Increment(ref _device_lock_handoffs);
            Interlocked.Add(ref _device_lock_yield_ticks, Stopwatch.GetTimestamp() - start);
        }
        bool outer = (uint)lockState[4] != GetCurrentThreadId();
        long entered = Stopwatch.GetTimestamp();
        new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockEnter, 0)).chain_from(h_device_lock_enter).fnptr!(guard);
        if (!outer) return;
        long now = Stopwatch.GetTimestamp();
        var stats = _device_lock_stats.GetOrAdd(GetCurrentThreadId(), _ => new LockStats());
        Interlocked.Add(ref stats.Wait, now - entered);
        Interlocked.Increment(ref stats.Entries);
        _device_lock_held_since = now;
    }
}
