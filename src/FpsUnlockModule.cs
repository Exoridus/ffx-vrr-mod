namespace Fahrenheit.Mods.FpsUnlock;

/// <summary>
///     Runs the engine at the display refresh rate and retimes the frame-bound systems that would
///     otherwise run at double speed.
///
///     Hooks are addressed by RVA (see <see cref="EngineAddresses"/>) because the pinned Fahrenheit's
///     generated call table exposes most engine functions only as FUN_&lt;address&gt; entries.
/// </summary>
[FhLoad(FhGameId.FFX)]
public unsafe sealed partial class FpsUnlockModule : FhModule
{
    private readonly PatchJournal _patches = new();
    private FpsUnlockConfig _config = new();

    /// <summary>
    ///     The one setting this mod puts in Fahrenheit's panel. Everything else in FpsUnlockConfig is a
    ///     per-correction kill switch or a probe, built so a bad correction can be bisected in a
    ///     running game, and those stay in the mod's own JSON: they are a diagnostic surface, not a
    ///     preference. Registering all 43 here would render 43 rows inline, because
    ///     FhSettingsCategory has no working collapse yet - its render carries a TODO for the arrow
    ///     button and never sets `collapsed`.
    ///
    ///     30 is off in the only sense that matters to this mod: scale is target/30, so at 30 every
    ///     hold stops holding and every scaled duration is left alone. The hooks stay installed.
    ///
    ///     Step 30 so clicking walks 0 / 30 / 60 / 90 / 120; any rate up to 240 can be typed, and
    ///     adopt_measured_rate accepts anything from 20 to 400.
    ///
    ///     0 means measure the display instead, which is the default and what the mod did before
    ///     this setting existed. It has to be a sentinel rather than an absent value because a
    ///     number setting always holds one, and without it the measured path would be dead code.
    /// </summary>
    private readonly FhSettingNumber<int> _target_framerate =
        new("fhfpsunlock.target_framerate", 0, 0, 240, 30);

    public FpsUnlockModule()
    {
        // Global rather than settings_local: a target framerate is a property of the display, not
        // of a save file.
        settings = new FhSettingsCategory("fhfpsunlock", [_target_framerate]);
    }

    /// <summary>
    ///     The rate to correct for, or null to measure the display. The JSON override wins over the
    ///     panel because it is the more specific statement, and it is what a diagnostic run sets.
    /// </summary>
    private double? ChosenFramerate =>
        _config.TargetFramerateOverride
        ?? (_target_framerate.get() > 0 ? _target_framerate.get() : null);

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

    /// <summary>
    ///     The rate every correction in this module is derived from, and nothing here assumes it is
    ///     60. It is the configured override, else the measured present rate snapped to a nominal
    ///     refresh rate, else what the limiter implies before the first measurement exists.
    ///
    ///     Measuring rather than assuming is what makes an unlocked framerate work: at interval 0
    ///     the engine presents as fast as it can, no global states the rate, and a scale derived
    ///     from a wrong number is worse than no scale at all.
    /// </summary>
    private static float TargetFramerate => _measured_framerate > 0
        ? _measured_framerate
        : 60f / Math.Max(1u, VSyncIntervalTarget);

    private static float _measured_framerate;
    private double _previous_sample_fps;

    /// <summary>
    ///     Nominal rates a display actually runs at. The measured value is snapped onto one of these
    ///     when it is close enough, because a scale that drifts with a frame time spike would make
    ///     every held sequence stutter; a rate far from all of them is used as measured.
    /// </summary>
    private static readonly float[] NominalRates = [30f, 50f, 60f, 72f, 75f, 90f, 100f, 120f, 144f, 165f, 240f];

    public override bool init(FhModContext mod_context, FileStream global_state_file)
    {
        string config_path = FpsUnlockConfig.ResolvePath();
        bool   config_found = File.Exists(config_path);

        _config = FpsUnlockConfig.Load(config_path, out string? config_error);

        // The engine has no shutdown callback, so the restore is bound to process exit. The parked
        // texture animation step bytes go back first: they are engine work-buffer state rather than
        // image bytes, so the journal knows nothing about them.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            restore_parked_texture_animation();
            _patches.RestoreAll();
        };

        bool ok = true;

        if (_config.Present)
        {
            ok &= hook_or_log("FFXApplication::animate", EngineAddresses.FFXApplicationAnimate,
                () => new FhMethodHandle<d_frame>(new FhMethodLocation(EngineAddresses.FFXApplicationAnimate, 0)).hook(this, h_frame));
        }

        if (_config.KeepVsyncInterval)
        {
            ok &= hook_or_log("SetFlipVSyncInterval", EngineAddresses.SetFlipVSyncInterval,
                () => new FhMethodHandle<d_set_vsync>(new FhMethodLocation(EngineAddresses.SetFlipVSyncInterval, 0)).hook(this, h_set_vsync));
        }

        if (_config.VideoTargetFramerate is { } fps)
        {
            // .rdata, so PatchJournal's VirtualProtect window is what makes it writable; the
            // journal also puts the original 29.97 back on process exit.
            _patches.Write(EngineAddresses.TextureVideoTargetFramerate, BitConverter.GetBytes(fps));
            _logger.Info($"[FpsUnlock] Video target framerate patched to {fps} at 0x{EngineAddresses.TextureVideoTargetFramerate:X}.");
        }

        ok &= init_timing_hooks();
        ok &= init_battle_hooks();
        ok &= init_frame_sequence_hooks();
        ok &= init_particle_hooks();
        ok &= init_effect_hooks();
        ok &= init_particle_kernel_hooks();
        ok &= init_motion_survey();
        ok &= init_survey_hooks();
        ok &= init_motion_sequence_hooks();
        ok &= init_lens_sprite_hook();
        ok &= init_cross_fade_hook();
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

        _sync_aware = _config.SyncDataAware;
        _vsync_interval_target = _config.PresentInterval;

        if (ChosenFramerate is { } target)
        {
            _measured_framerate = (float)target;
            _logger.Info($"[FpsUnlock] Target framerate {target:F2} "
                       + (_config.TargetFramerateOverride is null ? "from settings." : "forced by config.")
                       + (target <= 30.0 ? " At 30 the scale is 1: every correction is inert, though the hooks stay installed." : ""));
        }
        else
        {
            _logger.Info("[FpsUnlock] Target framerate set to measure the display.");
        }

        // Said first and unconditionally: a config that was looked for in the wrong directory
        // reads as a run with every default, and nothing else in this log distinguishes the two.
        if (config_error is not null)
            _logger.Error($"[FpsUnlock] Config at {config_path} could not be read, every setting is at its default: {config_error}");
        else
            _logger.Info(config_found
                ? $"[FpsUnlock] Config read from {config_path}."
                : $"[FpsUnlock] No config at {config_path} - every setting is at its default.");
        _logger.Info($"[FpsUnlock] Initialized. present={_config.Present} atel={_config.AtelWaits} camera={_config.Camera} " +
                     $"fades={_config.Fades} motion={_config.Motion} battle={_config.BattleTimers}");
        _logger.Info($"[FpsUnlock] Frame-sequence holds: menu_water={_config.MenuWater} fmv={_config.Fmv} " +
                     $"texture_animation={_config.TextureAnimation}. These are 30 Hz inside a 60 Hz game by " +
                     "design; the real fix is content at the target rate.");
        _logger.Info($"[FpsUnlock] Particles retimed on the object age: timeline={_config.ParticleTimeline} " +
                     $"kernel_hold={_config.ParticleKernelHold} integrator_scale={_config.ParticleIntegratorScale}.");

        return ok;
    }

    /// <summary>
    ///     Installs one hook and reports the failure with its address rather than only a false return,
    ///     so a bad RVA is distinguishable from a refused hook.
    /// </summary>
    private bool hook_or_log(string name, nint rva, Func<bool> install)
    {
        bool ok = install();
        if (!ok) _logger.Error($"[FpsUnlock] Could not hook {name} at RVA 0x{rva:X}.");
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
        VSyncInterval = VSyncIntervalTarget;
        _frames++;

        // Both before the engine's update for this frame: the first decides whether this frame
        // advances a held sequence, the second acts on that decision.
        decide_frame_advance();
        advance_overlay_spawn_clock();
        retime_texture_animation();
        sample_particle_population();
        sample_field_managers();

        // Not behind the telemetry flag. This is where the rate every correction derives from is
        // measured, and where the image patches are kept in step with it; only the log line it
        // emits is telemetry.
        sample_present_rate();
        apply_rate_patches();

        return new FhMethodHandle<d_frame>(new FhMethodLocation(EngineAddresses.FFXApplicationAnimate, 0))
            .chain_from(h_frame).fnptr!(ptr_this);
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

    /// <summary>True once the engine has been seen pacing itself from syncdata during this window.</summary>
    private bool _rate_window_sync_paced;
    private long _rate_windows_discarded;

    /// <summary>
    ///     How many sample windows the rate adoption refused, and whether the engine is pacing itself
    ///     from the syncdata table at the moment the line is written.
    ///
    ///     Cumulative rather than per window, unlike the counters around it: at most one window can be
    ///     discarded per line, so a per-window count would only ever read 0 or 1, and what a log has
    ///     to answer afterwards is whether the guard fired at all and how much of the session it ate.
    /// </summary>
    private string rate_guard_counts()
        => $"rate_guard={_rate_windows_discarded} sync={(EngineSyncPaced ? 1 : 0)}";

    /// <summary>
    ///     Measures the present rate over a five second window and hands it to the rate adoption.
    ///     Only the log line at the end is telemetry: the measurement itself is what
    ///     <see cref="TargetFramerate"/> is, so gating the whole method on the telemetry flag left
    ///     every correction running on the rate the limiter implied rather than the one the display
    ///     actually has.
    /// </summary>
    /* Every per-subsystem counter as one string. Shared by the periodic telemetry line and by the
     * overlay's marker button so the two can never drift into reporting different sets. */
    private string counter_snapshot()
        => $"{particle_counts()}, {effect_counts()}, {kernel_counts()}, {integrator_counts()}, {motion_counts()}, {survey_counts()}, " +
           $"{engine_state_counts()}, " +
           $"{motion_sequence_counts()}, " +
           $"{frame_sequence_counts()}, {lens_sprite_counts()}, " +
           $"{texture_animation_counts()} cam_acc={_camera_acc_calls} {motion_speed_counts()}, " +
           $"{cross_fade_counts()}, {atel_wait_counts()}, " +
           $"{particle_timeline_counts()}, {frame_skip_counts()}, {eternal_calm_counts()}, " +
           $"{idle_sway_counts()}, {neck_counts()}, {buoyancy_counts()}, " +
           $"{atel_worker_motion_counts()}, {ch_facing_counts()}, {ch_ramp_counts()}, " +
           $"{ch_pitch_counts()}, {asset_override_counts()}, {video_override_counts()}, " +
           $"{overlay_spawn_gate_counts()}, " +
           $"{field_restart_counts()}, " +
           $"{rate_guard_counts()}";

    private void sample_present_rate()
    {
        // Latched every frame rather than read at the window's two edges. A syncdata scene shorter
        // than the window would otherwise leave no trace in the one measurement it ruined, and a
        // window that only overlaps such a scene at one end measures a mixture that is not a
        // framerate either.
        if (EngineSyncPaced) _rate_window_sync_paced = true;

        TimeSpan now = _clock.Elapsed;
        if ((now - _last_sample).TotalSeconds < 5) return;

        double fps = (_frames - _frames_at_last_sample) / (now - _last_sample).TotalSeconds;

        adopt_measured_rate(fps);

        if (_config.Telemetry)
        {
            // t is ElapsedSeconds, the same clock the timer overlay draws - printed here regardless
            // of whether that overlay is on, so a symptom named by its on-screen value is always
            // findable by an exact string match rather than by arithmetic on two clocks.
            _logger.Info($"[FpsUnlock] t={ElapsedSeconds:F1} present {fps:F1} fps over the last {(now - _last_sample).TotalSeconds:F1}s, " +
                         $"vsync_interval={VSyncInterval}, keep_fps={KeepFps}, " +
                         $"sg_ratef={FhUtil.get_at<float>(EngineAddresses.SgRateF):F3}, " +
                         $"{counter_snapshot()}");
        }

        // After the adoption has read it, so the window the flag describes is the one just measured.
        _rate_window_sync_paced = false;
        _frames_at_last_sample = _frames;
        _last_sample = now;
    }

    /// <summary>
    ///     Takes the measured present rate as the rate to correct for, once it is stable enough to
    ///     trust. A loading screen or a stall would otherwise move the scale, and a scale that moves
    ///     retimes a sequence that is already running. A window that overlapped a syncdata scene is
    ///     discarded outright, because the engine throttles presentation itself there and the number
    ///     measured is the recording's cadence rather than the display's.
    /// </summary>
    private void adopt_measured_rate(double fps)
    {
        // A chosen rate is a statement, not a measurement, so no window can revise it. Read every
        // sample rather than once at init: the panel setting can change while the game runs, and a
        // rate that only took effect on restart would make the setting look broken.
        if (ChosenFramerate is { } forced)
        {
            if (Math.Abs(_measured_framerate - forced) > 0.01f)
            {
                _measured_framerate = (float)forced;
                _logger.Info($"[FpsUnlock] Target framerate now {forced:F2}"
                           + (_config.TargetFramerateOverride is null ? " (settings)." : " (forced by config).")
                           + (forced <= 30.0 ? " Scale is 1: every correction is inert." : ""));
            }

            return;
        }

        // A window that touched a syncdata scene is not a measurement of the display's rate, and it
        // is discarded rather than merely disbelieved.
        //
        // While g_isNeedSync is set, FUN_00821f90 spins until real time has caught up with the
        // recorded PS2 frame times whenever the recording is ahead, and updateFFX does not return to
        // this module's chain point until the whole catch-up count is drained - so presentation, not
        // just the simulation, runs at the recording's roughly 30 Hz cadence. Two such windows agree
        // with each other to well within five percent, which is all the pairing test asks for, and
        // the rate would snap to a nominal 30. Scale would then be 1 for the rest of the session:
        // every hold stops holding, every scaled duration stops being scaled, and since this method
        // is the only writer of the rate, nothing observes the scene ending.
        //
        // Zeroing the previous sample is the other half of it. The pairing test compares adjacent
        // windows, and a clean window either side of a discarded one is not adjacent to anything.
        if (_rate_window_sync_paced)
        {
            _rate_windows_discarded++;
            _previous_sample_fps = 0;
            return;
        }

        if (fps < 20 || fps > 400) return;

        // A rate is adopted only once two consecutive samples agree. The first sample of a run falls
        // inside the boot load and measured 46.7 fps once, which snapped to 50 and set a scale of
        // 1.67 for a game that was about to run at 60.
        double previous = _previous_sample_fps;
        _previous_sample_fps = fps;

        if (previous <= 0 || Math.Abs(fps - previous) / fps > 0.05) return;

        float nominal = NominalRates.MinBy(r => Math.Abs(r - fps));
        float adopted = Math.Abs(nominal - fps) / nominal < 0.08f ? nominal : (float)fps;

        // Only a change worth reacting to moves the scale, so an ordinary sample does nothing.
        if (Math.Abs(adopted - _measured_framerate) / Math.Max(1f, adopted) < 0.05f) return;

        _logger.Info($"[FpsUnlock] Target framerate {(_measured_framerate == 0 ? "set" : "changed")} to " +
                     $"{adopted:F2} from a measured {fps:F1} fps. Scale is now {adopted / 30f:F2}.");

        _measured_framerate = adopted;
    }

    /// <summary>
    ///     Re-derives the three image patches, once per presented frame.
    ///
    ///     Every one of them takes its value from <see cref="Scale"/>, and Scale carries the syncdata
    ///     term as well as the measured rate. That term flips whenever a scene with recorded PS2
    ///     frame times starts or ends, which is far more often than the measured rate moves - so
    ///     re-deriving only on a rate change leaves a byte in .text that disagrees with the value the
    ///     rest of the module is using, with no second trigger to correct it. A byte cannot follow a
    ///     per-scene state unless something rewrites it.
    ///
    ///     Each of the three is a compare and a return while its value is unchanged, so the cost of
    ///     asking every frame is three float divisions. The rate is not asked for before one has been
    ///     adopted: at init it is only implied by the limiter, and with the limiter removed that
    ///     implication is wrong.
    /// </summary>
    private void apply_rate_patches()
    {
        if (_measured_framerate <= 0) return;

        patch_particle_timeline();
        patch_battle_cursor_blink();
        patch_dream_overlay_stars();
        patch_move_lead_in();
        patch_ch_facing_rates();
    }
}
