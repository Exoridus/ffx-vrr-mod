global using System.Diagnostics;
global using System.Text.Json;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
using Xunit;

namespace Fahrenheit.Mods.Vrr;

[Collection("Render scheduler")]
public sealed class RenderSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_owner_selected_between_passes_discards_fractional_history(bool movie)
    {
        var module = new VrrModule();
        module.PreparePass();
        module.NativeMovie = movie;
        module.NativeSync = !movie;
        module.RunClock();
        AssertNativeOwnership(module);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_owner_selected_inside_clock_discards_fractional_history(bool movie)
    {
        var module = new VrrModule();
        module.PreparePass();
        NativeClock.Advance = () =>
        {
            module.NativeMovie = movie;
            module.NativeSync = !movie;
        };
        try
        {
            module.RunClock();
            AssertNativeOwnership(module);
        }
        finally { NativeClock.Advance = null; }
    }

    [Theory]
    [InlineData(null, false, 0f)]
    [InlineData(30d, false, 30f)]
    [InlineData(60d, false, 60f)]
    [InlineData(120d, false, 120f)]
    [InlineData(144d, false, 144f)]
    [InlineData(0d, true, 60f)]
    [InlineData(120d, true, 60f)]
    public void Reported_presentation_rate_matches_the_supported_pacing_rate(double? chosen, bool movie, float expected)
    {
        var module = new VrrModule { ChosenRate = chosen, NativeMovie = movie };
        module.PreparePass();
        module.SetMaximum((int)(chosen ?? 0));
        Assert.Equal(expected, module.ReportedRate);
    }

    [Fact]
    public void UnqualifiedProducerRetainsNativeClockAndBoundedPresentation()
    {
        var module = new VrrModule { ProducerQualified = false };
        module.PreparePass();
        module.SetMaximum(0);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        Assert.Equal(60, module.ReportedRate);
        module.RunClock();
        Assert.False(RenderPassContext.IsPresentation);
        Assert.False(RenderPassContext.IsFinalImage);
    }

    [Fact]
    public void InstalledFieldPauseEnablesPresentationUntilANativeOwnerVetoes()
    {
        var module = new VrrModule();
        Assert.True(module.EnableAdapter().InitScheduler());
        module.ArmScheduler();
        Assert.Equal(1, module.FieldPauseInstalls);
        module.BeginFrame();
        Assert.True(RenderPassContext.IsActive);
        module.NativeMovie = true;
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
        module.NativeMovie = false;
        module.NativeSync = true;
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
    }

    [Fact]
    public void RefusedFieldPauseKeepsTheNativeClock()
    {
        var module = new VrrModule { ProducerQualified = false };
        Assert.True(module.EnableAdapter().InitScheduler());
        module.ArmScheduler();
        Assert.Equal(1, module.FieldPauseInstalls);
        module.BeginFrame();
        Assert.False(RenderPassContext.IsActive);
    }

    [Fact]
    public void NativePassStillPublishesItsOwnSimulationIdentity()
    {
        var module = new VrrModule();
        module.DisableScheduler();
        long serial = RenderPassContext.SimulationSerial;
        module.RunNativePass();
        Assert.Equal(serial + 1, RenderPassContext.SimulationSerial);
    }

    private static void AssertNativeOwnership(VrrModule module)
    {
        Assert.False(RenderPassContext.IsActive);
        Assert.False(RenderPassContext.IsPresentation);
        Assert.Equal(1f, module.CameraAlpha);
        Assert.True(module.CameraResets > 0);
        Assert.True(module.ActorResets > 0);
    }
}

[CollectionDefinition("Render scheduler", DisableParallelization = true)]
public sealed class RenderSchedulerCollection;

public unsafe sealed partial class VrrModule
{
    public VrrModule() { _pad_latch_installed = _pad_history_readers_ready = true; SeedSyntheticSnapshotBaseline(); ArmSyntheticRuntimeForTest(); }
    private void ArmSyntheticRuntimeForTest()
    {
        // Synthetic fixture activation does not qualify native startup.
        _startupActivation.Prepare(1);
        if (!_startupActivation.Invoke(1, true, () => { }, () => true, () => { }))
            throw new InvalidOperationException("Synthetic fixture activation failed.");
    }
    internal void StopSyntheticRuntimeForTest() => _startupActivation.Stop();
    internal bool SyntheticSnapshotReady = true;
    private void SeedSyntheticSnapshotBaseline()
    {
        _padSnapshotInstalled = _padSnapshotBaseline = true;
        _padSnapshotBaselineEpoch = RenderPassContext.HistoryEpoch;
        _render_snapshot_ready_at_frame = true;
        _render_snapshot_warmup = false;
    }
    internal VrrModule EnableAdapter() { IndependentPresentationEnabled = true; return this; }
    private static uint _vsync_interval_target;
    internal int SharedTextureObservers, IndependentSwapchainHooks;
    private bool init_texture_device_observer() { SharedTextureObservers++; return true; }
    private bool install_vrr_swapchain_hook() { IndependentSwapchainHooks++; return true; }
    internal bool InitPresenter() => init_vrr_presenter();
    internal int WarningCount => _logger.Warnings.Count;
    internal void RequestIndependent(bool requested) => _config = _config with { VrrEnabled = requested };
    internal bool Preflight() => prepare_presentation_startup();
    internal bool Effective => IndependentPresentationEnabled;
    internal bool Requested => _config.VrrEnabled;
    internal uint Limiter => _vsync_interval_target;
    internal string VetoReason => _independentQualification.Reason;
    internal bool NativeMovie, NativeSync;
    internal bool ProducerQualified = true;
    private bool init_particle_presentation() => true;
    private void particle_presentation_enter_loop() { }
    internal int FieldPauseInstalls;
    private bool init_field_particle_pause() { FieldPauseInstalls++; return ProducerQualified; }
    private bool field_particle_presentation_ready() => ProducerQualified;
    internal bool ImageReady = true;
    private bool render_image_admission_ready() => ImageReady;
    internal int AdmittedImages;
    internal bool CommitImage = true;
    private bool render_image_admission_begin() { if (!CommitImage) return false; AdmittedImages++; return true; }
    private bool _fieldPauseInstalled => ProducerQualified;
    private bool _battleEffectPauseInstalled;
    internal byte BattleHold => _bx[0];
    internal bool BattlePauseNeedsCleanup => _bxPresented;
    internal int BattlePauseInitCalls;
    internal bool BattlePauseInstalled => _battleEffectPauseInstalled;
    private bool init_battle_effect_pause() { BattlePauseInitCalls++; return true; }
    private void battle_effects_begin_pass(bool presentation) { }
    private string _qualificationRefusal = "";
    private bool _filter_presentation_hold = true;
    private void viewport_snapshot() => sphere_probe_enter();
    private void viewport_presentation_begin() { }
    private void viewport_restore(bool presentation) => sphere_probe_exit(presentation);
    private void party_swap_collect() { }
    private void party_swap_end_pass(bool presentation) => DiagnosticCleanup?.Invoke();
    internal void BeginFrame() => render_scheduler_begin_frame();
    internal void RefusePresentation(string reason) => _presentationAdmission.Refuse(reason);
    internal void EndPass() => render_scheduler_exit_loop();
    internal void ArmScheduler() => _render_mode_ready = true;
    internal void SetHookScope(bool armed, bool frame, bool inside, bool presentation)
    {
        _render_mode_ready = armed;
        _render_scheduler_active = true;
        _render_frame_enabled = frame;
        _render_inside_loop = inside;
        _render_owner_thread = Environment.CurrentManagedThreadId;
        RenderPassContext.IsActive = frame;
        RenderPassContext.IsPresentation = presentation;
    }
    private readonly byte[] _testBattleData = GC.AllocateArray<byte>(0x20, pinned: true);
    internal void SetBattlePause(byte previous) { _battleEffectPauseInstalled = true; fixed (byte* data = _testBattleData) _bx = data; _bx[0] = previous; s_battle_effects = this; _particle_pilot_thread = Environment.CurrentManagedThreadId; }
    private void refuse_independent_presentation(string reason) => _presentationAdmission.Refuse(reason);
    internal uint ReadExec() => h_render_exec();
    internal void CalculateRate() => h_render_calc_rate();
    internal void Emit() => h_render_particle_emit(123);
    internal bool InitScheduler() => init_render_scheduler();
    internal double? ChosenRate;
    internal float CameraAlpha;
    internal int CameraResets, ActorResets;
    internal int CameraCompletions, ActorCompletions;
    internal (long SimulationTicks, long AdditionalImages, int Remaining) CompletionState
        => (_simulation_ticks, _additional_images, _render_steps_remaining);
    internal float ReportedRate => PresentationTargetHz;
    internal void SetMaximum(int rate) => _max_framerate.Value = rate;
    private double? ChosenFramerate => ChosenRate;
    private static float TargetFramerate => 60;
    private readonly TestRateSetting _max_framerate = new();
    private bool EngineSyncPaced => NativeSync;
    private bool fmv_playing() => NativeMovie;
    private SchedulerConfig _config = new();
    private readonly SchedulerLogger _logger = new();

    internal string? RejectedHook;
    private bool ensure_test_hook_registration()
    {
        if (_hookRegistration is not null) return true;
        VrrConfig config = new VrrConfig { VrrEnabled = _config.VrrEnabled, ParticleTimeline = _config.ParticleTimeline, EffectHold = _config.EffectHold };
        var selections = Enum.GetValues<HookSiteId>().Where(HookSites.IsDeclared)
            .Select(site => HookRegistrationPlan.ResolveSelection(site, config, IndependentPresentationEnabled, true, false, null));
        return HookRegistrationPlan.TryCreate(selections, HookRegistrationPlan.AvailableSourceContracts,
            out _hookRegistration, out _);
    }

    private bool hook_or_log(HookSiteId site, string name, nint rva, Func<bool> hook)
        => ensure_test_hook_registration() && register_hook(site, name, rva, hook) && name != RejectedHook;
    internal Action<string>? DiagnosticEventAction;
    private void diagnostic_event(string kind, object data)
    {
        DiagnosticEventAction?.Invoke(kind);
        DiagnosticEvents.Add((kind, data, _diagnosticLoopId, Environment.CurrentManagedThreadId));
    }
    internal int CameraInitCalls, ActorInitCalls;
    private bool init_render_camera() { CameraInitCalls++; return true; }
    private bool init_render_actors() { ActorInitCalls++; return true; }
    private void reset_render_camera() => CameraResets++;
    private void reset_render_actors() => ActorResets++;
    private void restore_render_camera() { }
    private void render_actors_restore() { }
    private void render_camera_complete_simulation() => CameraCompletions++;
    private void render_actors_complete_simulation() => ActorCompletions++;
    private void render_camera_set_presentation_alpha(float alpha) => CameraAlpha = alpha;
    private void render_camera_begin_simulation() { }
    private void render_actors_begin_simulation() { }
    private void render_camera_begin_presentation() { }
    private void render_actors_begin_presentation(float alpha) { }
    private void advance_overlay_spawn_clock() { if (_advance_frame) OverlayTicks++; }
    internal void PreparePass()
    {
        if (SyntheticSnapshotReady) SeedSyntheticSnapshotBaseline();
        _render_mode_ready = true;
        _render_scheduler_active = _render_frame_enabled = _render_mode_ready;
        _render_inside_loop = true;
        _render_owner_thread = Environment.CurrentManagedThreadId;
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = false;
        _render_plan = new(1, 0.5f, false, 0);
        _render_steps_remaining = 1;
    }
    internal int OverlayTicks, FrameSkipSamples;
    private long _fps_simulated;
    private float Scale => NativeSync ? 1f : TargetFramerate / 30f;
    private TestDiagnostics? _diagnostics;
    private void sample_frame_skip() => FrameSkipSamples++;
    private object diagnostic_state() => new object();
    internal bool RunNativePass() { h_advance_vblank(1f / TargetFramerate); return _advance_frame; }
    internal void DisableScheduler() { _render_scheduler_active = false; RenderPassContext.IsPresentation = false; RenderPassContext.IsActive = false; }
    internal double HoldCarry => _hold_carry;
    internal void PrepareCatchUp() { PreparePass(); _render_plan = new(2, 0.5f, false, 0); _render_steps_remaining = 2; }
    internal void SetRemainingPasses(int count) => _render_steps_remaining = count;
    internal void PreparePresentation() { PreparePass(); _render_steps_remaining = 0; }
    internal bool RunClock()
    {
        render_scheduler_clock(1f / 60);
        return _advance_frame;
    }
}

internal sealed partial record SchedulerConfig
{
    public int TargetFramerateOverride { get; init; }
    public bool VrrEnabled { get; init; } = true;
    public uint PresentInterval => 0;
    public bool VBlankRate => true;
    public bool CharacterDelta => true;
    public bool ParticleTimeline => true;
    public bool EffectHold { get; init; } = true;
}
internal sealed class SchedulerLogger { public readonly List<string> Warnings = []; public void Error(string message) { } public void Info(string message) { } public void Warning(string message) => Warnings.Add(message); }
internal readonly record struct FhCallLocation(nint Rva, int Unused);
internal readonly struct FhCallHandle<T>(FhCallLocation location) where T : Delegate
{
    public T fnptr
    {
        get
        {
            _ = location;
            string method = typeof(T).Name switch
            {
                "d_pad_history" => location.Rva switch
                {
                    0x488e80 => nameof(NativeClock.PressedHistory),
                    0x488ea0 => nameof(NativeClock.ReleasedHistory),
                    0x488e50 => nameof(NativeClock.RepeatHistory),
                    0x488e30 => nameof(NativeClock.Repeat2History),
                    _ => throw new InvalidOperationException("Unknown pad history accessor"),
                },
                "d_menu_edge" => location.Rva == 0x4be4b0 ? nameof(NativeClock.MenuPressed) : nameof(NativeClock.MenuRepeat),
                "d_effect_process" => nameof(NativeClock.Effect),
                "d_eternal_run" => nameof(NativeClock.Eternal),
                "d_render_exec" => nameof(NativeClock.Exec),
                "d_pad_port_poll" => nameof(NativeClock.Poll),
                "d_render_void" => location.Rva == 0x42d070 ? nameof(NativeClock.Snapshot) : location.Rva == 0x4be500 ? nameof(NativeClock.MenuCacheUpdate) : location.Rva == 0x4893e0 ? nameof(NativeClock.Pad) : nameof(NativeClock.Calculate),
                "d_render_emit" => nameof(NativeClock.Emit),
                "d_diag_exec" => nameof(NativeClock.Exec),
                "d_diag_loop" => nameof(NativeClock.Loop),
                _ => nameof(NativeClock.Tick),
            };
            return (T)Delegate.CreateDelegate(typeof(T), typeof(NativeClock).GetMethod(method)!);
        }
    }
    public bool hook(object owner, T callback) { NativeClock.HistoryHookAction?.Invoke(); NativeClock.HistoryHookRegistrations.Add((location.Rva, callback)); NativeClock.Hooks.Add(typeof(T).Name); return true; }
    public FhCallHandle<T> chain_from(T callback)
    {
        if (location.Rva == 0x42d070 && !ReferenceEquals(callback, NativeSnapshotBridge.Entry))
            throw new InvalidOperationException("Snapshot chain must use the rooted native entry delegate.");
        return this;
    }
}
internal static partial class NativeClock
{
    internal static Action? Advance;
    public static void Tick(float delta) => Advance?.Invoke();
}
internal static unsafe class FhUtil
{
    internal static readonly List<(nint Address, object? Value)> Writes = [];
    public static void set_at<T>(nint rva, T value) => Writes.Add((rva, value));
    internal static nint SphereEngine;
    internal static nint PadRecordPointer;
    internal static int Overdrive, MenuPause, MainMenu;
    internal static int MenuReservation = -1;
    internal static byte Dummy;
    public static T* ptr_at<T>(nint rva) where T : unmanaged
    {
        if (NativeSnapshotFixture.TryPointer<T>(rva, out nint snapshot)) return (T*)snapshot;
        if (PadRecordPointer == 0 || rva < 0xf30288 || rva >= 0xf30488) throw new InvalidOperationException("Unmapped pad fixture");
        return (T*)(PadRecordPointer + rva - 0xf30288);
    }
    public static T get_at<T>(nint rva) => rva == 0x1F05834 ? (T)(object)SphereEngine : rva == EngineAddresses.PlayerChrs ? (T)(object)NativeActorPool.Pointer
        : rva == 0xf3c930 ? (T)(object)Overdrive
        : rva == 0xefbc38 ? (T)(object)MenuReservation
        : rva == 0x146a430 ? (T)(object)Dummy
        : rva == 0xf40824 ? (T)(object)MainMenu : rva == 0xf40828 ? (T)(object)MenuPause : default!;
}
internal static partial class EngineAddresses
{
    public const nint ElapsedFrameCountFrameSkip = 0, PpvUserStopPartF = 0, AdvanceVBlankCounters = 0, SgVCount = 0, SgVCount2 = 0, FilterActiveMask = 0, BattleState = 0;
}

internal sealed class TestRateSetting { internal int Value; internal int get() => Value; }

internal sealed class TestDiagnostics { internal bool IsCapturing { get; set; } internal long DroppedEvents { get; set; } }
