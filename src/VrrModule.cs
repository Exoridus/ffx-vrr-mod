namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Runs the engine at the display refresh rate and retimes the frame-bound systems that would
///     otherwise run at double speed.
///
///     Hooks are addressed by RVA (see <see cref="EngineAddresses"/>) because the pinned Fahrenheit's
///     generated call table exposes most engine functions only as FUN_&lt;address&gt; entries.
/// </summary>
[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class VrrModule : FhModule
{
    private TimingOwnership? _timing_ownership;
    private bool _initialized;
    private readonly PatchJournal _patches = new();
    private VrrConfig _config = new();

    /// <summary>
    ///     Simulation rate selected at startup. The legacy JSON override takes precedence.
    /// </summary>
    private readonly SimulationRateSetting _target_framerate = new();
    private readonly MaximumRateSetting _max_framerate = new();
    private readonly FhSettingToggle _fps_overlay = new("fhvrr.fps_overlay", true);

    public VrrModule()
    {
        // Global rather than settings_local: timing settings belong to the installation rather than
        // to an individual save file.
        _ = new Settings(this);
    }

    /// <summary>
    ///     Requested simulation rate, with the legacy JSON override taking precedence over the
    ///     panel because it is the more specific statement, and it is what a diagnostic run sets.
    /// </summary>
    private string? _target_rate_warning;

    private double? ChosenFramerate
    {
        get
        {
            double? chosen = _config.ResolveTargetFramerate(_target_framerate.get(), out string? warning);
            if (warning != _target_rate_warning)
            {
                _target_rate_warning = warning;
                if (warning is not null) _logger.Warning($"[Vrr] {warning}");
            }
            return chosen;
        }
    }

    /// <summary>The engine's KEEP_FPS flag, read where it lives.</summary>
    private static sbyte KeepFps => FhUtil.get_at<sbyte>(EngineAddresses.SgKeepFps);

    /// <summary>Target framerate divisor: the engine runs at 60 / this. 1 is 60 FPS, 2 is 30.</summary>
    private static uint VSyncInterval
    {
        get => FhUtil.get_at<uint>(EngineAddresses.SFlipVSyncInterval);
        set => FhUtil.set_at(EngineAddresses.SFlipVSyncInterval, value);
    }

    /// <summary>
    ///     What the module writes into the engine's limiter. 1 is 59.94 Hz, 2 is half that, and 0
    ///     removes the limit entirely - the WinMain pump multiplies this by one frame at 59.94 to
    ///     get the time it must sleep, so zero means it never sleeps.
    /// </summary>
    private static uint _vsync_interval_target = 1;
    private static uint VSyncIntervalTarget => _vsync_interval_target;

    /// <summary>The simulation rate selected at startup; render measurements never change it.</summary>
    private static float TargetFramerate => _simulation_framerate > 0
        ? _simulation_framerate
        : 60f / Math.Max(1u, VSyncIntervalTarget);

    private static float _simulation_framerate;

    public override bool init(FhModContext mod_context, FileStream global_state_file)
    {
        if (!NativeBuild.TryValidate(out string nativeError))
        {
            _logger.Error(nativeError);
            return false;
        }

        string config_path = VrrConfig.ResolvePath();
        bool   config_found = File.Exists(config_path);

        _config = VrrConfig.Load(config_path, out string? config_error, out string? config_warning);
        if (config_error is not null)
            _logger.Error($"[Vrr] Config at {config_path} could not be read, every setting is at its default: {config_error}");
        else
            _logger.Info(config_found
                ? $"[Vrr] Config read from {config_path}."
                : $"[Vrr] No config at {config_path} - every setting is at its default.");
        if (config_warning is not null) _logger.Warning($"[Vrr] Config at {config_path}: {config_warning}");
        _sync_aware = _config.SyncDataAware;
        _vsync_interval_target = _config.VrrEnabled ? _config.PresentInterval : ChosenFramerate == 30d ? 2u : 1u;
        if (!_config.VrrEnabled && ChosenFramerate == 120d)
        {
            _logger.Error("[Vrr] 120 Hz simulation requires the independent presentation path. No timing hooks installed.");
            return false;
        }
        _simulation_framerate = (float)(ChosenFramerate ?? 60d);
        if (!float.IsFinite(_simulation_framerate) || _simulation_framerate <= 0)
        {
            _logger.Error("[Vrr] An unlocked limiter requires an explicit correction-rate override before initialization. No timing hooks installed.");
            return false;
        }
        if (!_config.Present)
        {
            _logger.Error("[Vrr] Timing hooks require the presentation driver. No timing hooks installed.");
            return false;
        }
        if (!_config.VrrEnabled && ChosenFramerate is { } correction && _vsync_interval_target > 0
            && Math.Abs(correction - 60.0 / _vsync_interval_target) > 1)
            _logger.Error("[Vrr] Correction-rate override differs from the engine limiter. The override does not change presentation pacing; this configuration is unsupported without an external pacing owner.");


        if (!preflight_expanded_menu_water()) return false;

        if (!TimingOwnership.TryAcquire(out _timing_ownership))
        {
            _logger.Error("[Vrr] Another FPS/VRR module already owns engine timing. The first initialized module retains ownership; this module installs no hooks.");
            return false;
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cleanup_runtime();

        try
        {
            init_diagnostics();
            bool ok = true;

            if (_config.Present)
            {
                ok &= hook_or_log("FFXApplication::animate", EngineAddresses.FFXApplicationAnimate,
                    () => new FhCallHandle<d_frame>(new FhCallLocation(EngineAddresses.FFXApplicationAnimate, 0)).hook(this, h_frame));
            }

            if (_config.KeepVsyncInterval)
            {
                ok &= hook_or_log("SetFlipVSyncInterval", EngineAddresses.SetFlipVSyncInterval,
                    () => new FhCallHandle<d_set_vsync>(new FhCallLocation(EngineAddresses.SetFlipVSyncInterval, 0)).hook(this, h_set_vsync));
            }

            if (_config.VideoTargetFramerate is not null)
                _logger.Error("[Vrr] VideoTargetFramerate is ignored: the authored movie-to-script clock must remain unchanged. Use a validated per-asset movie manifest for upload cadence.");

            ok &= init_timing_hooks();
            ok &= init_render_scheduler();
            ok &= init_vrr_presenter();
            ok &= init_battle_hooks();
            ok &= init_expanded_menu_water();
            ok &= init_frame_sequence_hooks();
            ok &= init_texture_animation_hook();
            ok &= init_texture_frame_interpolation();
            ok &= init_particle_hooks();
            ok &= init_effect_hooks();
            ok &= init_particle_kernel_hooks();
            ok &= init_motion_survey();
            ok &= init_survey_hooks();
            ok &= init_motion_sequence_hooks();
            ok &= init_lens_sprite_hook();
            ok &= init_particle_uv_hooks();
            ok &= init_cross_fade_hook();
            ok &= init_pad_latch_hook();
            ok &= init_viewport_rollback();
            ok &= init_upstream_ui_hooks();
            ok &= init_device_lock_fairness();
            ok &= init_eternal_calm_hook();
            ok &= init_idle_sway_hook();
            ok &= init_neck_tracking_hook();
            ok &= init_atel_worker_motion_hooks();
            ok &= init_ch_facing_patch();
            ok &= init_ch_shade_ramp_hook();
            ok &= init_ch_pitch_hook();
            ok &= init_asset_override();
            ok &= init_video_override();
            ok &= init_field_particle_restart_hook();

            // Before the game's own entry point runs, so before any magic overlay can have been loaded
            // and snapshotted the slot this rewrites.
            init_overlay_spawn_gate();

            if (ChosenFramerate is { } target)
            {
                _logger.Info($"[Vrr] Target framerate {target:F2} "
                           + (_config.TargetFramerateOverride is null ? "from settings." : "forced by config.")
                           + (target <= 30.0 ? " At 30 the scale is 1: every correction is inert, though the hooks stay installed." : ""));
            }
            else
            {
                _logger.Info("[Vrr] Simulation defaults to 60 Hz; render measurements do not change it.");
            }

            _logger.Info($"[Vrr] Initialized. present={_config.Present} atel={_config.AtelWaits} camera={_config.Camera} " +
                         $"fades={_config.Fades} motion={_config.Motion} battle={_config.BattleTimers}");
            _logger.Info($"[Vrr] Frame-sequence holds: menu_water={_config.MenuWater} fmv={_config.Fmv} " +
                         $"texture_animation={_config.TextureAnimation}. These are 30 Hz inside a 60 Hz game by " +
                         "design; the real fix is content at the target rate.");
            _logger.Info($"[Vrr] Particles retimed on the object age: timeline={_config.ParticleTimeline} " +
                         $"kernel_hold={_config.ParticleKernelHold} integrator_scale={_config.ParticleIntegratorScale}.");

            apply_rate_patches();
            _render_mode_ready = ok && _render_scheduler_active;
            if (_config.VrrEnabled && !_render_mode_ready)
            {
                _render_scheduler_active = false;
                _logger.Error("[Vrr] Independent presentation was not armed because a required initialization failed. The native swapchain and 60 Hz limiter are retained.");
            }
            _initialized = ok;
            diagnostic_event("initialization_complete", new { success = ok, independent_presentation_ready = _render_mode_ready });
            if (!ok) cleanup_runtime(failedStartup: true);
            return ok;
        }
        catch (Exception error)
        {
            _logger.Error($"[Vrr] Initialization failed: {error}");
            cleanup_runtime(failedStartup: true);
            return false;
        }
    }

    private void cleanup_runtime(bool failedStartup = false)
    {
        _initialized = false;
        bool waterRestored = false;
        try { restore_expanded_menu_water(); waterRestored = true; }
        catch (Exception error) { _logger.Error($"[Vrr] Menu water restoration failed: {error}"); }
        bool restored = false;
        try { _patches.RestoreAll(); restored = true; }
        catch (Exception error) { _logger.Error($"[Vrr] Patch restoration failed; referenced movie storage retained: {error}"); }
        // Continuing startup with partial native patches can call freed state or mix timing owners.
        if (failedStartup && (!waterRestored || !restored))
            Environment.FailFast("VRR initialization failed and native patches could not be restored safely.");
        if (restored)
        {
            try { release_fmv_surfaces(); }
            catch (Exception error) { _logger.Error($"[Vrr] Movie storage cleanup failed: {error}"); }
        }
        try { _timing_ownership?.Dispose(); }
        catch (Exception error) { _logger.Error($"[Vrr] Timing ownership release failed: {error}"); }
        _timing_ownership = null;
    }

    /// <summary>
    ///     Installs one hook and reports the failure with its address rather than only a false return,
    ///     so a bad RVA is distinguishable from a refused hook.
    /// </summary>
    private bool hook_or_log(string name, nint rva, Func<bool> install)
    {
        bool ok = install();
        diagnostic_event("timing_hook", new { name, rva = (long)rva, installed = ok });
        if (!ok) _logger.Error($"[Vrr] Could not hook {name} at RVA 0x{rva:X}." );
        return ok;
    }

    // --- Present path ---

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate uint d_frame(nint ptr_this);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_set_vsync(uint interval);

    /* The engine retimes its own display loop off the flip vsync interval. Setting it to 1 rather
     * than 2 is what actually produces 60 FPS; everything else in this module compensates for it. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvThiscall)])]
    private uint h_frame(nint ptr_this)
    {
        _frames++;
        MemoryProbe.NextFrame();
        diagnostic_frame_begin();
        render_scheduler_begin_frame();
        trace_battle_state();
        load_telemetry_frame();
        // Unlocking the engine limiter is only valid while the render scheduler owns the clock; otherwise the
        // native clock would run faster than 60 Hz. Pacing then falls to PresentationTargetHz.
        VSyncInterval = _config.VrrEnabled ? (_render_frame_enabled ? 0u : 1u) : VSyncIntervalTarget;
        if (_config.VrrEnabled) presenter_pace(_frame_pacer);
        sample_present_rate();
        apply_rate_patches();

        // Without the clock hook, diagnostic runs retain one hold decision per outer update.
        if (!_config.VBlankRate)
        {
            decide_frame_advance();
            advance_overlay_spawn_clock();
        }
        sample_particle_population();
        sample_field_managers();

        try
        {
            return new FhCallHandle<d_frame>(new FhCallLocation(EngineAddresses.FFXApplicationAnimate, 0))
                .chain_from(h_frame).fnptr!(ptr_this);
        }
        finally
        {
            render_scheduler_end_frame();
            diagnostic_raw(2);
            if (_diagnostics?.IsCapturing == true)
                diagnostic_event("animate_exit", new { duration_ms = Stopwatch.GetElapsedTime(_diagnosticFrameStart).TotalMilliseconds, state = diagnostic_state() });
        }
    }

    /* The engine sets the interval to 1 in menus and 2 everywhere else. Since this module owns it,
     * every request to change it is dropped rather than chained. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_set_vsync(uint interval) { }

    // --- Telemetry ---

    private long _frames;
    private long _frames_at_last_sample;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _last_sample;

    /// <summary>
    ///     Seconds since this module's clock started. The one source the timer overlay draws and
    ///     the telemetry line logs, so a value read off the screen and a value read off the log
    ///     for the same instant always agree.
    /// </summary>
    private double ElapsedSeconds => _clock.Elapsed.TotalSeconds;

    private string rate_guard_counts() => $"sync={(EngineSyncPaced ? 1 : 0)}";

    /* Every per-subsystem counter as one string. Shared by the periodic telemetry line and by the
     * overlay's marker button so the two can never drift into reporting different sets. */
    private string counter_snapshot()
        => $"{particle_counts()}, {effect_counts()}, {kernel_counts()}, {integrator_counts()}, {motion_counts()}, {survey_counts()}, " +
           $"{engine_state_counts()}, " +
           $"{motion_sequence_counts()}, " +
           $"{frame_sequence_counts()}, {lens_sprite_counts()}, " +
           $"{texture_animation_counts()} cam_acc={_camera_acc_calls} {motion_speed_counts()}, " +
           $"{cross_fade_counts()}, {filter_hold_counts()}, {pad_latch_counts()}, {viewport_rollback_counts()}, {upstream_ui_counts()}, {load_telemetry_counts()}, {atel_wait_counts()}, " +
           $"{particle_timeline_counts()}, {frame_skip_counts()}, {eternal_calm_counts()}, " +
           $"{idle_sway_counts()}, {neck_counts()}, {buoyancy_counts()}, " +
           $"{atel_worker_motion_counts()}, {ch_facing_counts()}, {ch_ramp_counts()}, " +
           $"{ch_pitch_counts()}, {asset_override_counts()}, {video_override_counts()}, " +
           $"{overlay_spawn_gate_counts()}, " +
           $"{field_restart_counts()}, " +
           $"{battle_effect_counts()}, " +
           $"{rate_guard_counts()}, sim_ticks={System.Threading.Interlocked.Read(ref _simulation_ticks)} extra_images={System.Threading.Interlocked.Read(ref _additional_images)}";

    private void sample_present_rate()
    {

        TimeSpan now = _clock.Elapsed;
        if ((now - _last_sample).TotalSeconds < 5) return;

        long samples = _frames;
        double fps = (samples - _frames_at_last_sample) / (now - _last_sample).TotalSeconds;

        validate_simulation_rate_request();

        if (_config.Telemetry)
        {
            // t is ElapsedSeconds, the same clock the timer overlay draws - printed here regardless
            // of whether that overlay is on, so a symptom named by its on-screen value is always
            // findable by an exact string match rather than by arithmetic on two clocks.
            _logger.Info($"[Vrr] t={ElapsedSeconds:F1} animate calls {fps:F1}/s over the last {(now - _last_sample).TotalSeconds:F1}s, " +
                         $"vsync_interval={VSyncInterval}, keep_fps={KeepFps}, render_gate={(_render_frame_enabled ? "open" : "closed:" + _render_gate_reason())}, " +
                         $"sg_ratef={FhUtil.get_at<float>(EngineAddresses.SgRateF):F3}, " +
                         $"{counter_snapshot()}");
        }

        _frames_at_last_sample = samples;
        _last_sample = now;
    }

    private double? _pending_simulation_rate;

    private void validate_simulation_rate_request()
    {
        double requested = ChosenFramerate ?? 60;
        if (Math.Abs(requested - _simulation_framerate) < 0.01)
        {
            _pending_simulation_rate = null;
            return;
        }
        if (_pending_simulation_rate == requested) return;
        _pending_simulation_rate = requested;
        _logger.Warning($"[Vrr] Simulation {requested:F0} Hz selected for next startup; current simulation remains {_simulation_framerate:F0} Hz.");
    }
    // Syncdata pacing can change the correction scale without changing the selected simulation rate.
    private void apply_rate_patches()
    {
        if (_simulation_framerate <= 0) return;

        patch_particle_timeline();
        patch_battle_cursor_blink();
        patch_dream_overlay_stars();
        patch_move_lead_in();
        patch_ch_facing_rates();
    }

    private sealed class SimulationRateSetting() : FhSettingNumber<int>("fhvrr.simulation_framerate", 60, 30, 120, 30)
    {
        private int _previous = 60;
        public new int get() => _previous = _value = VrrConfig.NormalizeSimulationFramerate(_value, _previous);
        public override void render_name() { get(); base.render_name(); }
    }

    private sealed class MaximumRateSetting() : FhSettingNumber<int>("fhvrr.max_framerate", 0, 0, int.MaxValue, 1)
    {
        private int _previous;
        public new int get() => _previous = _value = VrrConfig.NormalizeMaxFramerate(_value, _previous);
        public override void render_name() { get(); base.render_name(); }
    }
    private sealed class Settings(VrrModule module) : FhSettingProvider<VrrModule>
    {
        protected override IEnumerable<FhSetting> get()
            => [module._target_framerate, module._max_framerate, module._fps_overlay];
    }
}
