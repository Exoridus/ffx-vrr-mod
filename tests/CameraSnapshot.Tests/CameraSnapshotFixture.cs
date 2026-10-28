global using System.Diagnostics;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
global using System.Text.Json;
using System.Numerics;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    public VrrModule()
    {
        // Synthetic fixture activation does not qualify native startup.
        _startupActivation.Prepare(1);
        if (!_startupActivation.Invoke(1, true, () => { }, () => true, () => { }))
            throw new InvalidOperationException("Synthetic fixture activation failed.");
    }

    internal void StopSyntheticRuntimeForTest() => _startupActivation.Stop();


    private readonly VrrConfig _config = new();
    private readonly TestLogger _logger = new();
    private readonly TestSetting _max_framerate = new();
    private bool IndependentPresentationEnabled => true;
    private bool EngineSyncPaced => NativeOwner;
    private float TargetFramerate => 60;
    private float Scale => 2;
    private bool _fieldPauseInstalled = true, _filter_presentation_hold = true;
    private string _qualificationRefusal = "";
    private TestDiagnostics? _diagnostics = null;
    private long _fps_simulated;
    private const nint DiagnosticMainLoop = 0x420000;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_diag_loop(float delta);

    internal bool NativeOwner;
    internal bool Baseline => _padSnapshotBaseline;
    internal bool Held => _padSnapshotHeld;
    internal bool Presentation => RenderPassContext.IsPresentation;
    internal string? Failure => _presentationAdmission.Failure;

    internal void BindSyntheticCamera()
    {
        _renderCameraView = CameraNative.View;
        _renderCameraSource2 = CameraNative.Source2;
        _renderCameraSource3 = CameraNative.Source3;
        _renderCameraCurrent = CameraNative.Current;
        _renderCameraBlocks = CameraNative.Blocks;
        _renderCameraBound = true;
        _render_mode_ready = _render_scheduler_active = true;
        _padSnapshotInstalled = true;
    }

    internal void Pass(double now, Action body)
    {
        render_scheduler_begin_frame();
        _render_now = now;
        var scope = pad_snapshot_enter_loop();
        bool successful = false;
        try
        {
            render_scheduler_enter_loop(1f / 60);
            h_advance_vblank(1f / 60);
            body();
            h_render_exec();
            render_scheduler_exit_loop();
            successful = true;
        }
        finally { pad_snapshot_exit_loop(scope, successful); }
    }

    internal void Snapshot() => pad_snapshot_invoke((nuint)FhUtil.ptr_at<byte>(PadSnapshotContinuation), CameraNative.Snapshot);
    internal int FinalizeCamera(int viewport = 0) => h_render_camera_finalize(viewport);
    internal void RestoreCamera() => restore_render_camera();
    private bool pad_native_input_owner() => NativeOwner;
    private object? _padReplayLoan => null;
    private void pad_replay_before_snapshot() { }
    private bool cooperative_ticks_ready() => true;
    private bool _padPublicationNativeFallback => false;
    private void pad_native_clock_invoke(float delta) => CameraNative.Resolve<d_advance_vblank>(EngineAddresses.AdvanceVBlankCounters)(delta);
    private void cooperative_tick_clock_completed(bool successful) { }
    private void cooperative_tick_snapshot_completed(nuint returnAddress, bool tracked) { }
    private bool pad_input_presentation_ready() => true;
    private bool pad_observation_binding_ready() => true;
    private bool fmv_playing() => NativeOwner;
    private bool render_image_admission_ready() => true;
    private bool render_image_admission_begin() => true;
    private bool field_particle_presentation_ready() => true;
    private bool init_particle_presentation() => true;
    private void init_field_particle_pause() { }
    private bool init_render_actors() => true;
    private void h_diag_loop(float delta) { }
    private void reset_pad_observation_timeline(string reason) { }
    private void reset_render_actors() { }
    private void render_actors_restore() { }
    private void render_actors_complete_simulation() { }
    private void render_actors_begin_simulation() { }
    private void render_actors_begin_presentation(float alpha) { }
    private void particle_presentation_enter_loop() { }
    private void viewport_snapshot() { }
    private void viewport_restore(bool presentation) { }
    private void viewport_presentation_begin() { }
    private void party_swap_collect() { }
    private void party_swap_end_pass(bool presentation) { }
    private void pad_latch_end_pass() { }
    private void pad_latch_pass_decided(bool presentation) { }
    private void battle_effects_begin_pass(bool presentation) { }
    private void battle_effects_end_pass() { }
    private void pad_observations_prepare_tick(double? deadline) { }
    private object? pad_observations_pass_completed(double? deadline) => null;
    private void advance_overlay_spawn_clock() { }
    private void sample_frame_skip() { }
    private object diagnostic_state() => new();
    private void diagnostic_event(string kind, object value) { }
    private void refuse_independent_presentation(string reason) => _presentationAdmission.Refuse(reason);
    private bool fx_code_matches(nint rva, string bytes) => rva == 0x420df0 && bytes == "E8 7B C2 00 00";
    private bool fx_call_is(nint rva, nint target) => rva == 0x420df0 && target == 0x42d070;
    private bool hook_or_log(HookSiteId site, string name, nint target, Func<bool> install) => register_hook(site, name, target, install);
    private sealed class TestSetting { internal int get() => 120; }
    private sealed class TestDiagnostics { internal bool IsCapturing => false; }
    private sealed class TestLogger
    {
        internal void Error(string message) { }
        internal void Warning(string message) { }
        internal void Info(string message) { }
    }
}

internal readonly record struct FhCallLocation(nint Address, int Offset = 0);
internal unsafe ref struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
{
    internal T fnptr => CameraNative.Resolve<T>(location.Address);
    internal FhCallHandle<T> chain_from(T callback) => this;
    internal bool hook(VrrModule owner, T callback) => throw new InvalidOperationException("Synthetic binding does not install native hooks.");
}

internal static class NativeBuild
{
    internal static bool TryValidate(out string error) { error = "No PE image in this fixture."; return false; }
}

internal static class NativeSnapshotBridge
{
    internal delegate void Dispatch(nuint returnAddress, nuint returnSlot);
    internal static bool TryCreate<T>(Dispatch dispatch, out T? entry) where T : Delegate { entry = null; return false; }
}

internal static class EngineAddresses
{
    internal const nint AdvanceVBlankCounters = 0x100, SgVCount = 0x104, SgVCount2 = 0x108,
        ElapsedFrameCountFrameSkip = 0x10c, PpvUserStopPartF = 0x110, FilterActiveMask = 0x114;
}

internal static unsafe class FhUtil
{
    internal static T* ptr_at<T>(nint rva) where T : unmanaged => (T*)CameraNative.Route(rva, typeof(T));
    internal static T get_at<T>(nint rva) where T : unmanaged => *ptr_at<T>(rva);
    internal static void set_at<T>(nint rva, T value) where T : unmanaged => *ptr_at<T>(rva) = value;
}

internal static class MemoryProbe
{
    internal static bool IsReadable(nint address, int count) => CameraNative.IsReadable(address, count);
}

internal static unsafe class CameraNative
{
    private static readonly Dictionary<nint, (nint Address, Type Type)> Routes = [];
    private static readonly List<(nint Address, int Size)> Allocations = [];
    internal static nint View, Blocks, Source2, Source3, Current;
    internal static Matrix4x4 CanonicalView, OutputView;
    internal static Vector3 Projection, OutputProjection;
    internal static int FinalizeCalls, SnapshotCalls, ViewCalls, FovCalls, Source1Calls, Source2Calls, Source3Calls;
    internal static int LastViewport, Result;

    internal static void Reset()
    {
        Release();
        View = Allocate(64); Blocks = Allocate(3 * 0x1c0);
        Source2 = Allocate(4); Source3 = Allocate(4); Current = Allocate(4);
        foreach (nint rva in new nint[] { 0xd33350, 0x1fc44a8, 0x1fc44b4, 0x104, 0x108 }) Map<uint>(rva);
        foreach (nint rva in new nint[] { 0xf3c910, 0xf007b0, 0xf007b1, 0xf007b3, 0x420df5 }) Map<byte>(rva);
        foreach (nint rva in new nint[] { 0x10c, 0x110, 0x114 }) Map<int>(rva);
        CanonicalView = OutputView = Matrix4x4.Identity;
        Projection = new(1, 2, 3); OutputProjection = default;
        FinalizeCalls = SnapshotCalls = ViewCalls = FovCalls = Source1Calls = Source2Calls = Source3Calls = 0;
        LastViewport = -1; Result = 0;
        *(uint*)Current = 7;
        RenderPassContext.IsPresentation = RenderPassContext.IsActive = false;
        RenderPassContext.ResetHistory();
    }

    internal static void Release()
    {
        foreach (var allocation in Allocations) NativeMemory.Free((void*)allocation.Address);
        Allocations.Clear(); Routes.Clear();
    }

    private static nint Allocate(int size)
    {
        nint address = (nint)NativeMemory.AllocZeroed((nuint)size);
        Allocations.Add((address, size));
        return address;
    }
    private static void Map<T>(nint rva) where T : unmanaged => Routes.Add(rva, (Allocate(sizeof(T)), typeof(T)));
    internal static nint Route(nint rva, Type type)
    {
        if (!Routes.TryGetValue(rva, out var route) || route.Type != type)
            throw new InvalidOperationException($"Unmapped or mistyped RVA 0x{rva:X}: {type.Name}.");
        return route.Address;
    }
    internal static bool IsReadable(nint address, int count) => count > 0
        && Allocations.Any(a => address >= a.Address && address - a.Address <= a.Size - count);
    internal static void Source(int source)
    {
        *(uint*)Source2 = source == 2 ? 11u : 0;
        *(uint*)Source3 = source == 3 ? 13u : 0;
    }
    internal static void Pose(float x, float fov = 1)
    {
        CanonicalView = Matrix4x4.CreateTranslation(-x, 0, 0);
        Projection = new(fov, 2, 3);
    }
    internal static T Resolve<T>(nint rva) where T : Delegate
    {
        nint target = rva switch
        {
            0x3bc110 => (nint)(delegate* unmanaged[Cdecl]<int, int>)&Finalize,
            0x239860 => (nint)(delegate* unmanaged[Cdecl]<float*, void>)&ViewCommit,
            0x3bc980 => (nint)(delegate* unmanaged[Cdecl]<int, void>)&FovCommit,
            0x100 => (nint)(delegate* unmanaged[Cdecl]<float, void>)&Advance,
            0x42d7e0 => (nint)(delegate* unmanaged[Cdecl]<uint>)&Exec,
            _ => throw new InvalidOperationException($"Unmapped call RVA 0x{rva:X}.")
        };
        return Marshal.GetDelegateForFunctionPointer<T>(target);
    }
    internal static void Snapshot() => SnapshotCalls++;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Exec() => 1;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Advance(float delta) { *(uint*)Route(0x104, typeof(uint)) += 2; *(uint*)Route(0x108, typeof(uint)) += 2; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Finalize(int viewport)
    {
        FinalizeCalls++; LastViewport = viewport;
        if (Result != 0 || (uint)viewport > 2) return Result;
        *(Matrix4x4*)View = CanonicalView;
        float* block = (float*)(Blocks + viewport * 0x1c0);
        block[4] = Projection.X; block[5] = Projection.Y; block[7] = Projection.Z;
        if (*(uint*)Source2 != 0) { Source2Calls++; OutputView = *(Matrix4x4*)View; }
        else if (*(uint*)Source3 != 0) { Source3Calls++; CommitView((float*)View); }
        else { Source1Calls++; CommitFov(viewport); }
        return Result;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ViewCommit(float* view) => CommitView(view);
    private static void CommitView(float* view) { ViewCalls++; OutputView = *(Matrix4x4*)view; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FovCommit(int viewport) => CommitFov(viewport);
    private static void CommitFov(int viewport)
    {
        FovCalls++; OutputView = *(Matrix4x4*)View;
        float* block = (float*)(Blocks + viewport * 0x1c0);
        OutputProjection = new(block[4], block[5], block[7]);
    }
}
