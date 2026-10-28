namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private const nint StartupCookie = 0x54a224;
    private const nint StartupEntryContinuation = 0x5493c6;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_startup_void();
    private readonly object _startupPublicationGate = new();
    private NativeSnapshotBridge.Dispatch? _startupDispatch;
    private d_startup_void? _startupEntry;
    private uint _startupOwnerThread;
    private volatile bool _startupPrepared;
    private bool _startupRenderReady;
    private string? _startupRefusal;

    private bool prepare_startup_arm()
    {
        if (!fx_code_matches(StartupCookie, "55 8B EC 83 EC 14"))
        {
            _logger.Error("[Vrr] Startup cookie entry does not match the supported executable.");
            return false;
        }
        _startupOwnerThread = startup_native_thread();
        _startupDispatch = h_startup_dispatch;
        if (_startupOwnerThread != 0 && NativeSnapshotBridge.TryCreateStartup(_startupDispatch, out _startupEntry)) return true;
        _logger.Error("[Vrr] The owned startup caller bridge is unavailable.");
        return false;
    }

    private bool init_startup_hook()
        => hook_or_log(HookSiteId.ModuleStartupCookieInitialization, "startup cookie initialization", StartupCookie,
            () => new FhCallHandle<d_startup_void>(new FhCallLocation(StartupCookie, 0))
                .hook(this, with_hook_continuation<d_startup_void>(_startupEntry!)));

    private void begin_startup_preparation()
    {
        _patches.BeginPreparation();
        _menu_water_patches.BeginPreparation();
    }

    private void complete_startup_preparation(bool renderReady)
    {
        _startupRenderReady = renderReady;
        _startupActivation.Prepare(_startupOwnerThread);
        _startupPrepared = true;
    }

    private void cleanup_runtime(bool failedStartup = false)
    {
        stop_startup_activation();
        lock (_startupPublicationGate) cleanup_runtime_owned(failedStartup);
    }

    private void stop_startup_activation()
    {
        _startupPrepared = false;
        _startupActivation.Stop();
    }

    private bool startup_caller_matches(nuint returnAddress, nuint returnSlot)
    {
        if (IntPtr.Size != 4 || returnAddress < 5 || returnAddress > uint.MaxValue - 5u
            || returnSlot == 0 || returnSlot > uint.MaxValue) return false;
        Span<byte> caller = stackalloc byte[10];
        Span<byte> slot = stackalloc byte[4];
        if (!MemoryProbe.TryCopy(unchecked((nint)(returnAddress - 5)), caller)
            || !MemoryProbe.TryCopy(unchecked((nint)returnSlot), slot)) return false;
        return StartupCaller.Matches(caller, returnAddress,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(slot),
            (nuint)FhUtil.ptr_at<byte>(StartupCookie), (nuint)FhUtil.ptr_at<byte>(StartupEntryContinuation));
    }

    private bool startup_verify_continuations()
    {
        if (_hookRegistration is null)
        {
            _startupRefusal = "The owning hook registration plan is unavailable.";
            return false;
        }
        return _hookRegistration.TryVerifyRequiredContinuations(out _startupRefusal);
    }

    private void startup_apply_prepared()
    {
        lock (_startupPublicationGate)
        {
            if (!_startupPrepared) throw new InvalidOperationException("Startup publication was stopped.");
            if (_patches.HasPreparedOverlap(_menu_water_patches))
                throw new InvalidOperationException("Startup patch owners overlap.");
            _patches.ValidatePrepared();
            _menu_water_patches.ValidatePrepared();
            _patches.CommitPrepared();
            _menu_water_patches.CommitPrepared();
            _initialized = true;
            _render_mode_ready = _startupRenderReady;
        }
    }

    private bool startup_invoke(nuint returnAddress, nuint returnSlot, Action native)
    {
        uint thread = startup_native_thread();
        bool callerMatched = startup_caller_matches(returnAddress, returnSlot);
        bool expected = _startupPrepared && callerMatched && thread == _startupOwnerThread && !RuntimeEffectsArmed;
        bool armed = _startupActivation.Invoke(thread, callerMatched, native,
            startup_verify_continuations, startup_apply_prepared);
        if (expected && !armed)
            throw new InvalidOperationException(_startupRefusal ?? "Startup activation was refused before publication.");
        return armed;
    }

    private void h_startup_dispatch(nuint returnAddress, nuint returnSlot)
    {
        try
        {
            d_startup_void native = HookSites.Native(() => new FhCallHandle<d_startup_void>(new FhCallLocation(StartupCookie, 0))
                .chain_from(_startupEntry!).fnptr);
            if (startup_invoke(returnAddress, returnSlot, native.Invoke))
                diagnostic_event("startup_armed", new { own_continuations_verified = true, independent_presentation_ready = _render_mode_ready });
        }
        catch (Exception error)
        {
            _startupActivation.Stop();
            StartupFailure.Abort(() => _logger.Error($"[Vrr] Startup activation failed: {error}"),
                () => cleanup_runtime(failedStartup: true), message => Environment.FailFast(message, error));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static extern uint startup_native_thread();
}
