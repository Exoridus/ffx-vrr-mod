using System.Threading;
using TerraFX.Interop.DirectX;
using static TerraFX.Interop.DirectX.DirectX;

namespace Fahrenheit.Mods.Vrr;

[System.Runtime.Versioning.SupportedOSPlatform("windows6.1")]
public unsafe sealed partial class VrrModule
{
    private readonly Dictionary<nint, DxgiSwapchainState> _presenter_chains = [];
    private readonly Dictionary<(int Kind, nint Target), DxgiHooks> _presenter_hooks = [];
    private readonly Lock _presenter_lock = new();
    private long _present_attempts;
    private long _present_successes;
    private long _present_rejections;
    private long _last_present_timestamp;
    private volatile bool _presenter_active;
    private bool _presenter_initialized_device;
    [ThreadStatic] private static bool _inside_present;

    private void presenter_overlay_begin()
    {
        if (_presenter_active) _inside_present = true;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int d_create_swapchain(IDXGIAdapter* adapter, uint driverType, nint software,
        uint flags, uint* levels, uint levelCount, uint sdk, DXGI_SWAP_CHAIN_DESC* descriptor,
        IDXGISwapChain** swapchain, ID3D11Device** device, uint* selectedLevel, ID3D11DeviceContext** context);

    // The engine's own chain is a single-buffer DISCARD (blt) chain, which DWM composes ("Composed: Copy with
    // GPU GDI" in PresentMon): no tearing, so no monitor VRR, and a frame of composition latency. The flip
    // chain below replaces it. It was once disabled for a per-pass slowdown that turned out to be uncached
    // VirtualQuery calls, which hit the native chain just the same.
    private bool init_vrr_presenter()
        => init_texture_device_observer() & (!_config.VrrEnabled || hook_or_log("D3D11CreateDeviceAndSwapChain", 0,
            () => new FhCallHandle<d_create_swapchain>(new FhCallLocation("d3d11.dll", "D3D11CreateDeviceAndSwapChain"))
                .hook(this, h_create_swapchain)));

    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private int h_create_swapchain(IDXGIAdapter* adapter, uint driverType, nint software,
        uint flags, uint* levels, uint levelCount, uint sdk, DXGI_SWAP_CHAIN_DESC* descriptor,
        IDXGISwapChain** swapchain, ID3D11Device** device, uint* selectedLevel, ID3D11DeviceContext** context)
    {
        var next = new FhCallHandle<d_create_swapchain>(
            new FhCallLocation("d3d11.dll", "D3D11CreateDeviceAndSwapChain")).chain_from(h_create_swapchain).fnptr!;
        if (!_render_mode_ready)
        {
            int nativeResult = next(adapter, driverType, software, flags, levels, levelCount, sdk, descriptor,
                swapchain, device, selectedLevel, context);
            return nativeResult;
        }
        // The engine creator keeps this descriptor beside its output handles for the chain lifetime.
        bool engineDescriptor = descriptor != null && (nint)swapchain == (nint)descriptor - 4
            && (nint)device == (nint)descriptor - 12 && (nint)context == (nint)descriptor - 8;
        if (engineDescriptor && _presenter_initialized_device)
        {
            _presenter_active = false;
            _logger.Error("[Vrr] Repeated engine device initialization is unsupported by the pinned Fahrenheit overlay lifecycle. Restart with VrrEnabled disabled to use native device recovery.");
            diagnostic_event("presenter_device_recreation_refused", new { result = unchecked((int)0x80004001) });
            return unchecked((int)0x80004001);
        }
        PresentationDescriptor original = descriptor == null ? default : descriptor_policy(*descriptor);
        PresentationDescriptor proposed = original;
        string? reason = engineDescriptor ? null : "Creation does not use the validated engine-owned descriptor.";
        bool supported = engineDescriptor && DxgiPresentationPolicy.TryCreate(original,
            presenter_tearing_supported(adapter), out proposed, out reason);
        if (!supported)
        {
            _logger.Warning($"[Vrr] Native swapchain retained; VRR inactive: {reason}");
            diagnostic_event("presenter_creation_refused", new { reason, original });
            int nativeResult = next(adapter, driverType, software, flags, levels, levelCount, sdk, descriptor,
                swapchain, device, selectedLevel, context);
            return nativeResult;
        }
        DXGI_SWAP_CHAIN_DESC saved = *descriptor;
        descriptor->BufferCount = proposed.BufferCount;
        descriptor->SwapEffect = (DXGI_SWAP_EFFECT)proposed.SwapEffect;
        descriptor->Flags = proposed.Flags;
        // D3D11_CREATE_DEVICE_SINGLETHREADED would forbid the multithread protection Present relies on.
        int result = next(adapter, driverType, software, flags & ~1u, levels, levelCount, sdk, descriptor,
            swapchain, device, selectedLevel, context);
        if (result < 0)
        {
            *descriptor = saved;
            diagnostic_event("presenter_creation_failed", new { result });
            _logger.Error($"[Vrr] Flip swapchain creation failed (0x{result:X8}); the engine descriptor was restored.");
            return result;
        }
        if (*swapchain == null || *device == null || *context == null)
        {
            presenter_release_created(swapchain, device, context);
            *descriptor = saved;
            _logger.Error("[Vrr] Device creation returned incomplete handles; VRR is inactive.");
            result = unchecked((int)0x80004005);
            diagnostic_event("presenter_creation_incomplete", new { result });
            return result;
        }
        var state = new DxgiSwapchainState(*swapchain, *context, descriptor);
        state.Multithreaded = presenter_protect_context(*context);
        _logger.Info($"[Vrr] Device flags 0x{flags:X} (created with 0x{flags & ~1u:X}); multithread protection {(state.Multithreaded ? "on, device lock released during Present" : "unavailable, device lock held during Present")}.");
        if (!presenter_bind(state))
        {
            presenter_release_created(swapchain, device, context);
            *descriptor = saved;
            _logger.Error("[Vrr] Coordinated presentation hooks were refused. Device initialization aborted; disable VrrEnabled to use the native swapchain.");
            result = unchecked((int)0x80004005);
            diagnostic_event("presenter_binding_refused", new { original, result });
            return result;
        }
        lock (_presenter_lock)
        {
            _presenter_chains[(nint)(*swapchain)] = state;
            _presenter_initialized_device = true;
            _presenter_active = true;
        }
        diagnostic_event("presenter_created", new { swapchain = (long)(nint)(*swapchain), proposed });
        _logger.Info("[Vrr] Windowed flip-sequential presentation active: two single-sample buffers, tearing, coordinated resize and render-target rebinding. Monitor VRR engagement requires runtime verification.");
        return result;
    }

    private static PresentationDescriptor descriptor_policy(DXGI_SWAP_CHAIN_DESC descriptor)
        => new(descriptor.BufferCount, (uint)descriptor.SwapEffect, descriptor.Flags,
            descriptor.SampleDesc.Count, descriptor.SampleDesc.Quality, (uint)descriptor.BufferDesc.Format,
            descriptor.Windowed != 0);

    private static void presenter_release_created(IDXGISwapChain** swapchain, ID3D11Device** device,
        ID3D11DeviceContext** context)
    {
        if (*context != null) (*context)->ClearState();
        if (*swapchain != null) (*swapchain)->Release();
        if (*context != null)
        {
            (*context)->Flush();
            (*context)->Release();
        }
        if (*device != null) (*device)->Release();
        *swapchain = null;
        *context = null;
        *device = null;
    }

    private static bool presenter_protect_context(ID3D11DeviceContext* context)
    {
        ID3D11Multithread* multithread = null;
        Guid iid = typeof(ID3D11Multithread).GUID;
        if (context->QueryInterface(&iid, (void**)&multithread) < 0 || multithread == null) return false;
        multithread->SetMultithreadProtected(true);
        bool on = multithread->GetMultithreadProtected();
        multithread->Release();
        return on;
    }

    private static bool presenter_tearing_supported(IDXGIAdapter* adapter)
    {
        IDXGIFactory1* factory = null;
        IDXGIFactory5* factory5 = null;
        Guid iid = typeof(IDXGIFactory1).GUID;
        int result = adapter == null ? (int)CreateDXGIFactory1(&iid, (void**)&factory)
            : (int)adapter->GetParent(&iid, (void**)&factory);
        try
        {
            if (result < 0 || factory == null) return false;
            iid = typeof(IDXGIFactory5).GUID;
            if (factory->QueryInterface(&iid, (void**)&factory5) < 0 || factory5 == null) return false;
            int allow = 0;
            return factory5->CheckFeatureSupport(DXGI_FEATURE.DXGI_FEATURE_PRESENT_ALLOW_TEARING,
                &allow, sizeof(int)) >= 0 && allow != 0;
        }
        finally
        {
            if (factory5 != null) factory5->Release();
            if (factory != null) factory->Release();
        }
    }

    private bool presenter_bind(DxgiSwapchainState state)
    {
        lock (_presenter_lock)
        {
            nint* table = *(nint**)state.Swapchain;
            nint* context = *(nint**)state.Context;
            if ((_config.NativePhaseProbe || _config.EffectProbe) && _diagnostics is not null)
            {
                bool indexed = presenter_bind_target(6, context[12]);
                bool plain = presenter_bind_target(7, context[13]);
                diagnostic_event("water_gpu_hooks", new { indexed, plain, resource_slots = 8, event_limit = 6000 });
            }
            return presenter_bind_target(0, table[8]) && presenter_bind_target(1, table[13])
                && presenter_bind_target(2, table[10]) && presenter_bind_target(3, table[2])
                && presenter_bind_target(4, context[33]) && presenter_bind_target(5, context[110]);
        }
    }

    private bool presenter_bind_target(int kind, nint target)
    {
        if (_presenter_hooks.TryGetValue((kind, target), out var existing)) return existing.Installed;
        var hooks = new DxgiHooks(this, target);
        _presenter_hooks.Add((kind, target), hooks);
        return hooks.Install(kind);
    }

    private void presenter_forget(nint swapchain)
    {
        lock (_presenter_lock)
        {
            _presenter_chains.Remove(swapchain);
            _presenter_active = _presenter_chains.Values.Any(s => s.Lifecycle.Active);
        }
    }

    private DxgiSwapchainState? presenter_state(nint swapchain)
    {
        lock (_presenter_lock) return _presenter_chains.GetValueOrDefault(swapchain);
    }

    private readonly PresentationPacer _frame_pacer = new();

    private void presenter_pace(PresentationPacer pacer)
    {
        double rate = PresentationTargetHz;
        long deadline = pacer.NextDeadline(Stopwatch.GetTimestamp(), Stopwatch.Frequency, rate);
        while (true)
        {
            double remaining = (deadline - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remaining <= 0) return;
            if (remaining > 2) Thread.Sleep((int)(remaining - 1));
            else Thread.SpinWait(64);
        }
    }
}
