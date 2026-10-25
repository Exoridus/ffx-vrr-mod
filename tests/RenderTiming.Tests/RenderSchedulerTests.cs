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
        Assert.True(module.InitScheduler());
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
        Assert.True(module.InitScheduler());
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
    internal bool NativeMovie, NativeSync;
    internal bool ProducerQualified = true;
    private bool init_particle_presentation() => true;
    private void particle_presentation_enter_loop() { }
    internal int FieldPauseInstalls;
    private bool init_field_particle_pause() { FieldPauseInstalls++; return ProducerQualified; }
    private bool field_particle_presentation_ready() => ProducerQualified;
    private bool _fieldPauseInstalled => ProducerQualified;
    private bool _battleEffectPauseInstalled;
    private byte battle_effects_hold_begin() => 0;
    private void battle_effects_hold_end(byte previous) { }
    private bool init_battle_effect_pause() => true;
    private void battle_effects_begin_pass(bool presentation) { }
    private void battle_effects_end_pass() { }
    private string _qualificationRefusal = "";
    private bool _filter_presentation_hold = true;
    private void viewport_snapshot() { }
    private void viewport_presentation_begin() { }
    private void viewport_restore(bool presentation) { }
    private void party_swap_collect() { }
    private void party_swap_end_pass(bool presentation) { }
    private void pad_latch_pass_decided(bool presentation) { }
    private void pad_latch_end_pass() { }
    internal void BeginFrame() => render_scheduler_begin_frame();
    internal bool InitScheduler() => init_render_scheduler();
    internal double? ChosenRate;
    internal float CameraAlpha;
    internal int CameraResets, ActorResets;
    internal float ReportedRate => PresentationTargetHz;
    internal void SetMaximum(int rate) => _max_framerate.Value = rate;
    private double? ChosenFramerate => ChosenRate;
    private static float TargetFramerate => 60;
    private readonly TestRateSetting _max_framerate = new();
    private bool EngineSyncPaced => NativeSync;
    private bool fmv_playing() => NativeMovie;
    private SchedulerConfig _config = new();
    private readonly SchedulerLogger _logger = new();
    private const nint DiagnosticMainLoop = 0;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_diag_loop(float delta);
    private void h_diag_loop(float delta) { }
    private bool hook_or_log(string name, nint rva, Func<bool> hook) => hook();
    private void diagnostic_event(string kind, object data) { }
    private bool init_render_camera() => true;
    private bool init_render_actors() => true;
    private void reset_render_camera() => CameraResets++;
    private void reset_render_actors() => ActorResets++;
    private void restore_render_camera() { }
    private void render_actors_restore() { }
    private void render_camera_complete_simulation() { }
    private void render_actors_complete_simulation() { }
    private void render_camera_set_presentation_alpha(float alpha) => CameraAlpha = alpha;
    private void render_camera_begin_simulation() { }
    private void render_actors_begin_simulation() { }
    private void render_camera_begin_presentation() { }
    private void render_actors_begin_presentation(float alpha) { }
    private void advance_overlay_spawn_clock() { if (_advance_frame) OverlayTicks++; }
    internal void PreparePass()
    {
        _render_mode_ready = true;
        _render_scheduler_active = _render_frame_enabled = _render_mode_ready;
        RenderPassContext.IsActive = true;
        RenderPassContext.IsPresentation = false;
        _render_plan = new(1, 0.5f, false, 0);
        _render_steps_remaining = 1;
    }
    internal int OverlayTicks, FrameSkipSamples;
    private long _fps_simulated;
    private float Scale => NativeSync ? 1f : TargetFramerate / 30f;
    private readonly TestDiagnostics? _diagnostics = null;
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
    public bool VBlankRate => true;
    public bool CharacterDelta => true;
    public bool ParticleTimeline => true;
    public bool EffectHold { get; init; } = true;
}
internal sealed class SchedulerLogger { public void Error(string message) { } }
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
                "d_effect_process" => nameof(NativeClock.Effect),
                "d_eternal_run" => nameof(NativeClock.Eternal),
                _ => nameof(NativeClock.Tick),
            };
            return (T)Delegate.CreateDelegate(typeof(T), typeof(NativeClock).GetMethod(method)!);
        }
    }
    public bool hook(object owner, T callback) { NativeClock.Hooks.Add(typeof(T).Name); return true; }
    public FhCallHandle<T> chain_from(T callback) => this;
}
internal static partial class NativeClock
{
    internal static Action? Advance;
    public static void Tick(float delta) => Advance?.Invoke();
}
internal static class FhUtil
{
    public static void set_at<T>(nint rva, T value) { }
    public static T get_at<T>(nint rva) => rva == EngineAddresses.PlayerChrs ? (T)(object)NativeActorPool.Pointer : default!;
}
internal static partial class EngineAddresses
{
    public const nint ElapsedFrameCountFrameSkip = 0, PpvUserStopPartF = 0, AdvanceVBlankCounters = 0, SgVCount = 0, SgVCount2 = 0, FilterActiveMask = 0, BattleState = 0;
}

internal sealed class TestRateSetting { internal int Value; internal int get() => Value; }

internal sealed class TestDiagnostics { internal bool IsCapturing => false; }
