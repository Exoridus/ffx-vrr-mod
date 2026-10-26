using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private sealed class DxgiSwapchainState(IDXGISwapChain* swapchain, ID3D11DeviceContext* context,
        DXGI_SWAP_CHAIN_DESC* descriptor)
    {
        internal readonly IDXGISwapChain* Swapchain = swapchain;
        internal readonly ID3D11DeviceContext* Context = context;
        internal readonly DXGI_SWAP_CHAIN_DESC* Descriptor = descriptor;
        internal readonly PresentationLifecycle Lifecycle = new();
        internal readonly PresentationPacer Pacer = new();
        internal readonly PresentationBindings Bindings = new();
        internal uint CreationFlags = descriptor->Flags;
        internal bool Resizing;
        internal bool Multithreaded;
        internal bool ReportedFailure;
        internal void ClearBindings()
        {
            Bindings.Clear();
        }
    }

    private sealed class DxgiHooks(VrrModule owner, nint target)
    {
        private readonly VrrModule _owner = owner;
        private readonly nint _target = target;
        internal bool Installed { get; private set; }
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int d_present(IDXGISwapChain* swapchain, uint interval, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int d_resize(IDXGISwapChain* swapchain, uint count, uint width, uint height, DXGI_FORMAT format, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int d_fullscreen(IDXGISwapChain* swapchain, int fullscreen, IDXGIOutput* output);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint d_release(IDXGISwapChain* swapchain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_set_targets(ID3D11DeviceContext* context, uint count, ID3D11RenderTargetView** targets, ID3D11DepthStencilView* depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_clear_state(ID3D11DeviceContext* context);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_draw_indexed(ID3D11DeviceContext* context, uint count, uint start, int baseVertex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void d_draw(ID3D11DeviceContext* context, uint count, uint start);

        internal bool Install(int kind)
        {
            Installed = kind switch
            {
                0 => HookSites.Install(HookSiteId.DxgiPresent, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_present>(new FhCallLocation(_target)).hook(_owner, Present)),
                1 => HookSites.Install(HookSiteId.DxgiResize, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_resize>(new FhCallLocation(_target)).hook(_owner, Resize)),
                2 => HookSites.Install(HookSiteId.DxgiFullscreen, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_fullscreen>(new FhCallLocation(_target)).hook(_owner, Fullscreen)),
                3 => HookSites.Install(HookSiteId.DxgiRelease, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_release>(new FhCallLocation(_target)).hook(_owner, Release)),
                4 => HookSites.Install(HookSiteId.DxgiSetTargets, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_set_targets>(new FhCallLocation(_target)).hook(_owner, SetTargets)),
                5 => HookSites.Install(HookSiteId.DxgiClearState, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_clear_state>(new FhCallLocation(_target)).hook(_owner, ClearState)),
                6 => HookSites.Install(HookSiteId.DxgiDrawIndexed, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_draw_indexed>(new FhCallLocation(_target)).hook(_owner, DrawIndexed)),
                7 => HookSites.Install(HookSiteId.DxgiDraw, _owner._hookRegistration, _target, _owner._hookRegistrationGeneration, () => new FhCallHandle<d_draw>(new FhCallLocation(_target)).hook(_owner, Draw)),
                _ => false
            };
            return Installed;
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private int Present(IDXGISwapChain* swapchain, uint interval, uint flags)
        {
            var next = new FhCallHandle<d_present>(new FhCallLocation(_target)).chain_from(Present).fnptr!;
            DxgiSwapchainState? state = _owner.presenter_state((nint)swapchain);
            if (state is null) return next(swapchain, interval, flags);
            if (state.Resizing)
            {
                if (!_owner.render_image_rejected_for_present(flags)) return next(swapchain, interval, flags);
                Interlocked.Increment(ref _owner._present_rejections);
                return 0;
            }
            int fullscreen = 0;
            int modeResult = (int)swapchain->GetFullscreenState((BOOL*)&fullscreen, null);
            var args = state.Lifecycle.Active
                ? DxgiPresentationPolicy.Present(modeResult >= 0 && fullscreen == 0, interval, flags)
                : (Interval: interval, Flags: flags & ~0x200u);
            // Present runs inside the render command list; waiting in it must not keep loaders off the device lock.
            bool owned = _owner.device_lock_owned_once();
            bool released = owned && state.Multithreaded && _owner.device_lock_release();
            long paceStart = Stopwatch.GetTimestamp();
            if (state.Lifecycle.Active && (flags & 1) == 0) _owner.presenter_pace(state.Pacer);
            long callStart = Stopwatch.GetTimestamp();
            ID3D11RenderTargetView** targets = stackalloc ID3D11RenderTargetView*[8];
            ID3D11DepthStencilView* depth = (ID3D11DepthStencilView*)state.Bindings.Depth;
            uint count = state.Bindings.Count;
            // The engine owns the views until resize; retain them only across overlay/native Present.
            for (int i = 0; i < 8; i++)
            {
                targets[i] = (ID3D11RenderTargetView*)state.Bindings.Targets[i];
                if (targets[i] != null) targets[i]->AddRef();
            }
            if (depth != null) depth->AddRef();
            _inside_present = true;
            int result;
            bool rejected;
            try
            {
                rejected = _owner.render_image_rejected_for_present(flags);
                result = rejected ? 0 : next(swapchain, args.Interval, args.Flags);
            }
            finally
            {
                if (released) _owner.device_lock_reacquire();
                // Flip unbinds buffer 0, but the engine's cached state can suppress its next bind.
                state.Context->OMSetRenderTargets(count, targets, depth);
                for (int i = 0; i < 8; i++) if (targets[i] != null) targets[i]->Release();
                if (depth != null) depth->Release();
                _inside_present = false;
            }
            long timestamp = Stopwatch.GetTimestamp();
            if (rejected)
            {
                Interlocked.Increment(ref _owner._present_rejections);
                return 0;
            }
            _owner.present_telemetry(callStart - paceStart, timestamp - callStart, owned && !released, released);
            _owner.diagnostic_present(result, args.Interval, args.Flags, (nint)swapchain, timestamp);
            state.Lifecycle.CompletePresent(result, args.Flags, timestamp);
            Interlocked.Increment(ref _owner._present_attempts);
            if (result == 0 && (args.Flags & 1) == 0)
            {
                Interlocked.Increment(ref _owner._present_successes);
                Interlocked.Exchange(ref _owner._last_present_timestamp, timestamp);
            }
            if (result != 0 && !state.ReportedFailure)
            {
                state.ReportedFailure = true;
                _owner.diagnostic_event("presenter_result", new { result, interval = args.Interval, flags = args.Flags, active = state.Lifecycle.Active });
                _owner._logger.Warning($"[Vrr] Present returned 0x{result:X8}; active={state.Lifecycle.Active}.");
            }
            if (!state.Lifecycle.Active)
            {
                lock (_owner._presenter_lock)
                    _owner._presenter_active = _owner._presenter_chains.Values.Any(s => s.Lifecycle.Active);
            }
            return result;
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void DrawIndexed(ID3D11DeviceContext* context, uint count, uint start, int baseVertex)
        {
            _owner.diagnostic_gameover_gpu_draw(count);
            _owner.diagnostic_water_draw(context, "indexed", count, start, baseVertex);
            new FhCallHandle<d_draw_indexed>(new FhCallLocation(_target)).chain_from(DrawIndexed).fnptr!(context, count, start, baseVertex);
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void Draw(ID3D11DeviceContext* context, uint count, uint start)
        {
            _owner.diagnostic_gameover_gpu_draw(count);
            _owner.diagnostic_water_draw(context, "plain", count, start, 0);
            new FhCallHandle<d_draw>(new FhCallLocation(_target)).chain_from(Draw).fnptr!(context, count, start);
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private int Resize(IDXGISwapChain* swapchain, uint count, uint width, uint height, DXGI_FORMAT format, uint flags)
        {
            var next = new FhCallHandle<d_resize>(new FhCallLocation(_target)).chain_from(Resize).fnptr!;
            DxgiSwapchainState? state = _owner.presenter_state((nint)swapchain);
            if (state is null) return next(swapchain, count, width, height, format, flags);
            var args = DxgiPresentationPolicy.Resize(count, flags, state.CreationFlags);
            state.Resizing = true;
            state.ClearBindings();
            state.Context->OMSetRenderTargets(0, null, null);
            int result;
            try { result = next(swapchain, args.BufferCount, width, height, format, args.Flags); }
            finally { state.Resizing = false; }
            state.Lifecycle.CompleteResize(result);
            if (!state.Lifecycle.Active)
            {
                lock (_owner._presenter_lock)
                    _owner._presenter_active = _owner._presenter_chains.Values.Any(s => s.Lifecycle.Active);
                _owner._logger.Error($"[Vrr] ResizeBuffers failed (0x{result:X8}); VRR clock ownership released.");
            }
            if (result >= 0)
            {
                DXGI_SWAP_CHAIN_DESC actual;
                if (swapchain->GetDesc(&actual) >= 0) *state.Descriptor = actual;
            }
            _owner.diagnostic_event("presenter_resize", new { result, count = args.BufferCount, width, height, format = (uint)format, flags = args.Flags });
            return result;
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private int Fullscreen(IDXGISwapChain* swapchain, int fullscreen, IDXGIOutput* output)
        {
            if (fullscreen != 0 && _owner.presenter_state((nint)swapchain) is not null)
            {
                _owner._logger.Warning("[Vrr] Exclusive fullscreen request refused; select windowed or borderless mode for VRR.");
                _owner.diagnostic_event("presenter_exclusive_refused", new { swapchain = (long)(nint)swapchain });
                return unchecked((int)0x887A0022);
            }
            return new FhCallHandle<d_fullscreen>(new FhCallLocation(_target)).chain_from(Fullscreen).fnptr!(swapchain, fullscreen, output);
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private uint Release(IDXGISwapChain* swapchain)
        {
            uint references = new FhCallHandle<d_release>(new FhCallLocation(_target)).chain_from(Release).fnptr!(swapchain);
            if (references == 0) _owner.presenter_forget((nint)swapchain);
            return references;
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void SetTargets(ID3D11DeviceContext* context, uint count, ID3D11RenderTargetView** targets, ID3D11DepthStencilView* depth)
        {
            if (!_inside_present)
            {
                lock (_owner._presenter_lock)
                {
                    foreach (var state in _owner._presenter_chains.Values)
                    {
                        if (state.Context != context) continue;
                        state.Bindings.Record(targets == null ? ReadOnlySpan<nint>.Empty
                            : new ReadOnlySpan<nint>((nint*)targets, (int)Math.Min(8, count)),
                            (nint)depth, _inside_present, state.Resizing);
                    }
                }
            }
            new FhCallHandle<d_set_targets>(new FhCallLocation(_target)).chain_from(SetTargets).fnptr!(context, count, targets, depth);
        }

        [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
        private void ClearState(ID3D11DeviceContext* context)
        {
            if (!_inside_present)
            {
                lock (_owner._presenter_lock)
                    foreach (var state in _owner._presenter_chains.Values)
                        if (state.Context == context) state.ClearBindings();
            }
            new FhCallHandle<d_clear_state>(new FhCallLocation(_target)).chain_from(ClearState).fnptr!(context);
        }
    }
}
