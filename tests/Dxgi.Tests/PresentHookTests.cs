global using System.Diagnostics;
using System.Threading;
using System.Runtime.Versioning;
using Fahrenheit.Mods.Vrr;
using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;
using Xunit;

[assembly: SupportedOSPlatform("windows6.1")]

namespace Fahrenheit.Mods.Vrr
{
    public unsafe sealed partial class VrrModule
    {
        [ThreadStatic] private static bool _inside_present;
        private readonly object _presenter_lock = new();
        private readonly Dictionary<nint, DxgiSwapchainState> _presenter_chains = [];
        private bool _presenter_active;
        private long _present_attempts, _present_successes, _last_present_timestamp, _present_rejections;
        internal bool RejectImage, DeviceLockHeld = true;
        internal int LockReacquires, PresentTelemetry, PresentDiagnostics;
        internal Action? Pace;
        private bool render_image_rejected_for_present(uint flags) => (flags & 1) == 0 && RejectImage;
        private bool device_lock_owned_once() => DeviceLockHeld;
        private bool device_lock_release() { DeviceLockHeld = false; return true; }
        private void device_lock_reacquire() { LockReacquires++; DeviceLockHeld = true; }
        private void presenter_pace(PresentationPacer pacer) => Pace?.Invoke();
        private DxgiSwapchainState? presenter_state(nint swapchain) => _presenter_chains.GetValueOrDefault(swapchain);
        private void presenter_forget(nint swapchain) => _presenter_chains.Remove(swapchain);
        private void present_telemetry(long pace, long native, bool owned, bool released) => PresentTelemetry++;
        private void diagnostic_present(int result, uint interval, uint flags, nint swapchain, long timestamp) => PresentDiagnostics++;
        private void diagnostic_gameover_gpu_draw(uint count) { }
        private void diagnostic_water_draw(ID3D11DeviceContext* context, string kind, uint count, uint start, int vertex) { }
        internal bool BindPresent(nint swapchain, nint context, nint descriptor, nint target, bool resizing)
        {
            var state = new DxgiSwapchainState((IDXGISwapChain*)swapchain, (ID3D11DeviceContext*)context, (DXGI_SWAP_CHAIN_DESC*)descriptor)
                { Multithreaded = true, Resizing = resizing };
            _presenter_chains.Add(swapchain, state);
            _presenter_active = true;
            return new DxgiHooks(this, target).Install(0);
        }
        internal void RecordTargets(nint swapchain, nint target) => _presenter_chains[swapchain].Bindings.Record([target], target, false, false);
        internal (long Attempts, long Successes, long Rejections, long LifecycleAttempts, long Timestamp) PresentCounts(nint swapchain)
            => (_present_attempts, _present_successes, _present_rejections, _presenter_chains[swapchain].Lifecycle.Attempts, _last_present_timestamp);
    }
}

[Collection("Texture hooks")]
public unsafe sealed class PresentHookTests
{
    private static int _native, _binds, _adds, _releases;
    private static uint _interval, _flags;
    private const nint Target = 0x70ff;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int NativePresent(nint swapchain, uint interval, uint flags)
    { _native++; _interval = interval; _flags = flags; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Fullscreen(nint swapchain, BOOL* fullscreen, nint* output)
    { *fullscreen = 0; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void SetTargets(nint context, uint count, nint* targets, nint depth) => _binds++;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(nint view) { _adds++; return 2; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(nint view) { _releases++; return 1; }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> _memory = [];
        internal readonly VrrModule Module = new();
        internal readonly nint Swapchain;
        internal Fixture(bool resizing = false)
        {
            _native = _binds = _adds = _releases = 0;
            TextureDrawBridgeTests.Callbacks.Clear();
            TextureDrawBridgeTests.Originals.Clear();
            nint table = Allocate(12 * IntPtr.Size);
            ((nint*)table)[11] = (nint)(delegate* unmanaged[Stdcall]<nint, BOOL*, nint*, int>)&Fullscreen;
            Swapchain = Allocate(IntPtr.Size); *(nint*)Swapchain = table;
            nint contextTable = Allocate(34 * IntPtr.Size);
            ((nint*)contextTable)[33] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)&SetTargets;
            nint context = Allocate(IntPtr.Size); *(nint*)context = contextTable;
            nint descriptor = Allocate(sizeof(DXGI_SWAP_CHAIN_DESC));
            nint viewTable = Allocate(3 * IntPtr.Size);
            ((nint*)viewTable)[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
            ((nint*)viewTable)[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            nint view = Allocate(IntPtr.Size); *(nint*)view = viewTable;
            TextureDrawBridgeTests.Originals[Target] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, uint, int>)&NativePresent;
            Assert.True(Module.BindPresent(Swapchain, context, descriptor, Target, resizing));
            Module.RecordTargets(Swapchain, view);
        }
        private nint Allocate(int count) { nint address = (nint)NativeMemory.AllocZeroed((nuint)count); _memory.Add(address); return address; }
        internal int Present(uint flags = 0) => (int)TextureDrawBridgeTests.Callbacks[Target].DynamicInvoke(Swapchain, 1u, flags)!;
        public void Dispose()
        {
            TextureDrawBridgeTests.Callbacks.Clear();
            TextureDrawBridgeTests.Originals.Clear();
            foreach (nint memory in _memory) NativeMemory.Free((void*)memory);
        }
    }

    [Fact]
    public void Rejected_image_is_withheld_but_native_state_cleanup_still_runs()
    {
        using var fixture = new Fixture();
        fixture.Module.RejectImage = true;
        Assert.Equal(0, fixture.Present());
        Assert.Equal(0, _native);
        Assert.Equal(1, _binds);
        Assert.Equal(2, _adds);
        Assert.Equal(2, _releases);
        Assert.True(fixture.Module.DeviceLockHeld);
        Assert.Equal(1, fixture.Module.LockReacquires);
        Assert.Equal((0L, 0L, 1L, 0L, 0L), fixture.Module.PresentCounts(fixture.Swapchain));
        Assert.Equal(0, fixture.Module.PresentTelemetry);
        Assert.Equal(0, fixture.Module.PresentDiagnostics);
    }

    [Fact]
    public void Refusal_during_pacing_is_checked_before_native_present()
    {
        using var fixture = new Fixture();
        fixture.Module.Pace = () => fixture.Module.RejectImage = true;
        fixture.Present();
        Assert.Equal(0, _native);
    }

    [Fact]
    public void Resize_bypass_cannot_display_a_rejected_owned_image()
    {
        using var fixture = new Fixture(resizing: true);
        fixture.Module.RejectImage = true;
        fixture.Present();
        Assert.Equal(0, _native);
        Assert.Equal(0, _adds);
        Assert.Equal(1, fixture.Module.PresentCounts(fixture.Swapchain).Rejections);
    }

    [Fact]
    public void Test_present_remains_native_and_does_not_count_as_a_displayed_image()
    {
        using var fixture = new Fixture();
        fixture.Module.RejectImage = true;
        fixture.Present(1);
        Assert.Equal(1, _native);
        Assert.Equal(1u, _flags);
        Assert.Equal(1u, _interval);
        var counts = fixture.Module.PresentCounts(fixture.Swapchain);
        Assert.Equal(1, counts.Attempts);
        Assert.Equal(0, counts.Successes);
        Assert.Equal(0, counts.Rejections);
    }

    [Fact]
    public void Accepted_native_image_after_a_withheld_image_is_counted_normally()
    {
        using var fixture = new Fixture();
        fixture.Module.RejectImage = true;
        fixture.Present();
        fixture.Module.RejectImage = false;
        fixture.Present();
        Assert.Equal(1, _native);
        var counts = fixture.Module.PresentCounts(fixture.Swapchain);
        Assert.Equal(1, counts.Attempts);
        Assert.Equal(1, counts.Successes);
        Assert.Equal(1, counts.Rejections);
        Assert.Equal(1, counts.LifecycleAttempts);
        Assert.True(counts.Timestamp > 0);
    }
}
