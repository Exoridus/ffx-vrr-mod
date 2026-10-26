using System.Threading;

namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Phyre's global device lock (RVA 0x894EE4, a recursive PCriticalSectionBase: +0xC waiters,
///     +0x10 owner thread id, +0x14 recursion). PRenderThread::processRenderCommands holds it for a
///     whole frame's command list, and the flip command runs Present inside that list, so at 144 Hz
///     the render thread held it while Present waited out most of the frame and the scene loader's
///     resource jobs starved. The Present hook releases it around that wait; these hooks only measure
///     per-thread wait and hold for the transition telemetry.
/// </summary>
public unsafe sealed partial class VrrModule
{
    // FUN_00463e10 in the v3 decompilation: the scoped-lock constructor every one of its 82 callers uses.
    private const nint DeviceLockEnter = 0x63D50, DeviceLockLeave = 0x63E90, DeviceLock = 0x894EE4;
    private const string DeviceLockEnterPattern =
        "55 8B EC 6A FF 68 ?? ?? ?? ?? 64 A1 00 00 00 00 50 51 56 A1 ?? ?? ?? ?? 33 C5 50 8D 45 F4 64 A3 00 00 00 00 8B F1 89 75 F0 A1 ?? ?? ?? ?? A8 01 75 2D 83 C8 01 A3 ?? ?? ?? ?? B9";

    // Measurement: per thread, ticks spent waiting to enter and holding the lock (outermost level only).
    private sealed class LockStats { public long Wait, Hold, Entries; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, LockStats> _device_lock_stats = new();
    [ThreadStatic] private static long _device_lock_held_since;

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)] private delegate void d_device_lock_enter(nint guard);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private bool init_device_lock_fairness()
    {
        if (!IndependentPresentationEnabled) return true;
        if (!fx_code_matches(DeviceLockEnter, DeviceLockEnterPattern) || !fx_operand_is(DeviceLockEnter + 0x3B, DeviceLock))
        {
            _logger.Error("[Vrr] Phyre device lock bytes differ from build 25501027; Present keeps the device lock and loading stays frame-rate bound.");
            return true;
        }
        if (!fx_code_matches(DeviceLockLeave, "55 8B EC 6A FF 68") || !fx_operand_is(DeviceLockLeave + 0x42, DeviceLock))
        {
            _logger.Error("[Vrr] Phyre device lock release bytes differ from build 25501027; Present keeps the device lock.");
            return true;
        }
        _device_lock_verified = true;
        return hook_or_log(HookSiteId.DeviceLockFairnessPhyreDeviceLockTelemetry, "Phyre device lock telemetry", DeviceLockEnter,
                () => new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockEnter, 0)).hook(this, h_device_lock_enter))
            && hook_or_log(HookSiteId.DeviceLockFairnessPhyreDeviceLockRelease, "Phyre device lock release", DeviceLockLeave,
                () => new FhCallHandle<d_device_lock_enter>(new FhCallLocation(DeviceLockLeave, 0)).hook(this, h_device_lock_leave));
    }

    private bool _device_lock_verified;

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
        ((delegate* unmanaged[Thiscall]<nint*, void>)FhUtil.ptr_at<byte>(DeviceLockEnter))(&guard);
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
