namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private enum RenderClockAction { Native, Advanced, Presentation }
    private const nint RenderGetExec = 0x42d7e0;
    private const nint RenderCalcRate = 0x4206c0;
    private const nint RenderParticleEmit = 0x312b80;
    private const nint RenderRateInteger = 0x1fcbbec;
    private const nint RenderRateFloat = 0x1fcbbe8;
    private FixedSimulationClock _simulationClock = new();
    private readonly PresentationAdmission _presentationAdmission = new();
    private bool _render_scheduler_active, _render_simulation_pass, _render_clock_seen;
    private int _render_steps_remaining;
    private SimulationFramePlan _render_plan;
    private long _simulation_ticks, _additional_images, _simulation_debt_dropped;
    private double _render_previous_time;
    private volatile bool _render_frame_enabled;
    private volatile bool _render_mode_ready;
    private volatile bool _render_inside_loop;
    private int? _render_particle_pause_before;
    private bool _render_plan_pending;
    private double _render_now;
    private float _render_input_delta;
    private bool _render_native_owner_reported;
    private bool _render_snapshot_warmup, _render_snapshot_ready_at_frame;
    private double _render_clock_speed = 1;
    private int _render_owner_thread;
    private bool RenderSchedulerReady => _render_mode_ready && _render_scheduler_active;
    private bool IsOwnedRenderLoop => RenderSchedulerReady && _render_inside_loop
        && _render_owner_thread == Environment.CurrentManagedThreadId;
    private bool OwnsRenderPass => IsOwnedRenderLoop && _render_frame_enabled;
    private bool IsOwnedPresentationPass => OwnsRenderPass && RenderPassContext.IsPresentation;
    private float PresentationTargetHz
    {
        get
        {
            int maximum = VrrConfig.NormalizeMaxFramerate(_max_framerate.get());
            // Native movie playback retains its verified media-clock owner.
            return fmv_playing() || EngineSyncPaced || !_render_frame_enabled
                ? maximum == 0 ? 60 : Math.Min(60, maximum) : maximum;
        }
    }

    private bool init_render_scheduler()
    {
        if (!IndependentPresentationEnabled) return true;
        if (!init_particle_presentation()) return false;
        _simulationClock = new FixedSimulationClock((int)TargetFramerate);
        if (!_config.VBlankRate || !_config.CharacterDelta || !_config.ParticleTimeline)
        {
            _logger.Error("[Vrr] Independent presentation requires the existing vblank, character and particle-timeline corrections.");
            return false;
        }
        // A refusal is logged there and keeps every frame on the native clock.
        init_field_particle_pause();
        // Dynamic overlay patches lack unload-safe journal ownership; battle retains the native clock.
        bool ok = hook_or_log(HookSiteId.RenderSchedulerRenderMainLoop, "render main loop", DiagnosticMainLoop,
            () => new FhCallHandle<d_diag_loop>(new FhCallLocation(DiagnosticMainLoop, 0)).hook(this, with_hook_continuation<d_diag_loop>(h_diag_loop)));
        ok &= hook_or_log(HookSiteId.RenderSchedulerRenderExecCount, "render exec count", RenderGetExec,
            () => new FhCallHandle<d_render_exec>(new FhCallLocation(RenderGetExec, 0)).hook(this, with_hook_continuation<d_render_exec>(h_render_exec)));
        ok &= hook_or_log(HookSiteId.RenderSchedulerRenderCalculationRate, "render calculation rate", RenderCalcRate,
            () => new FhCallHandle<d_render_void>(new FhCallLocation(RenderCalcRate, 0)).hook(this, with_hook_continuation<d_render_void>(h_render_calc_rate)));
        ok &= hook_or_log(HookSiteId.RenderSchedulerRenderParticleEmission, "render particle emission", RenderParticleEmit,
            () => new FhCallHandle<d_render_emit>(new FhCallLocation(RenderParticleEmit, 0)).hook(this, with_hook_continuation<d_render_emit>(h_render_particle_emit)));
        ok &= init_render_camera();
        ok &= init_render_actors();
        _render_scheduler_active = ok;
        diagnostic_event("render_scheduler", new { installed = ok, simulation_hz = TargetFramerate, presentation_hz = PresentationTargetHz });
        return ok;
    }

    private string _render_gate_reason()
        => _presentationAdmission.Failure ?? (EngineSyncPaced ? "syncdata" : fmv_playing() ? "movie"
            : IndependentPresentationEnabled && !cooperative_ticks_ready() ? "cooperative_ticks_unready"
            : !pad_observation_binding_ready() ? "pad_observer_not_installed"
            : !pad_input_presentation_ready() ? "pad_history_not_installed"
            : pad_native_input_owner() ? "native_input_owner"
            : !_fieldPauseInstalled ? "field_pause_not_installed"
            : filter_needs_native_clock() ? "screen_filter" : _qualificationRefusal);

    // Only reached when the Sg_DrawFilter presentation hold was refused.
    private bool filter_needs_native_clock()
        => !_filter_presentation_hold && FhUtil.get_at<int>(EngineAddresses.FilterActiveMask) != 0;

    private bool render_scheduler_frame_eligible(bool allowNativeInputOwner = false)
        => _presentationAdmission.Failure is null && cooperative_ticks_ready() && render_image_admission_ready()
            && pad_observation_binding_ready() && pad_input_presentation_ready() && (allowNativeInputOwner || !pad_native_input_owner())
            && field_particle_presentation_ready() && !filter_needs_native_clock();

    private void render_scheduler_begin_frame()
    {
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        RenderPassContext.BeginImage(_render_previous_time > 0 ? Math.Clamp(now - _render_previous_time, 0, 0.25) : 1d / TargetFramerate);
        _render_previous_time = now;
        if (!RenderSchedulerReady)
        {
            _render_frame_enabled = false;
            RenderPassContext.IsActive = false;
            RenderPassContext.IsPresentation = false;
            return;
        }
        // Recorded syncdata and movie playback keep their existing clock owner until adapted.
        bool eligible = render_scheduler_frame_eligible();
        _render_snapshot_ready_at_frame = pad_snapshot_presentation_ready();
        _render_snapshot_warmup = eligible && !_render_snapshot_ready_at_frame;
        _render_frame_enabled = eligible && _render_snapshot_ready_at_frame;
        if (_render_snapshot_warmup)
        {
            _simulationClock.Reset();
            RenderPassContext.ResetHistory();
            reset_render_camera();
            reset_render_actors();
        }
        if (_render_frame_enabled) _render_frame_enabled = render_image_admission_begin();
        if (!_render_frame_enabled && !_render_native_owner_reported)
            diagnostic_event("native_clock_owner", new { syncdata = EngineSyncPaced, movie = fmv_playing() });
        _render_native_owner_reported = !_render_frame_enabled;
        RenderPassContext.IsActive = _render_frame_enabled;
        _render_now = now;
        _render_plan_pending = true;
    }

    private void render_scheduler_end_frame()
    {
        if (_render_scheduler_active && _render_plan_pending)
        {
            _simulationClock.Reset();
            reset_pad_observation_timeline("unconsumed_plan");
            RenderPassContext.ResetHistory();
            reset_render_camera();
            reset_render_actors();
        }
        RenderPassContext.IsPresentation = false;
        battle_effects_end_pass();
        _render_inside_loop = false;
        RenderPassContext.IsActive = false;
        RenderPassContext.SetPass(1, false);
    }

    private void render_scheduler_enter_loop(float delta)
    {
        if (!RenderSchedulerReady) return;
        System.Threading.Interlocked.CompareExchange(ref _render_owner_thread, Environment.CurrentManagedThreadId, 0);
        particle_presentation_enter_loop();
        _render_clock_seen = false;
        _render_particle_pause_before = null;
        _render_inside_loop = true;
        _render_input_delta = delta;
        RenderPassContext.IsPresentation = false;
        restore_render_camera();
        render_actors_restore();
        viewport_snapshot();
        party_swap_collect();
    }

    private void render_scheduler_exit_loop(bool nativeReturned = true)
    {
        // A nested return can clear the shared loop marker before its outer call fails.
        if (!nativeReturned && (RenderSchedulerReady || _render_inside_loop))
        {
            reset_pad_observation_timeline("native_loop_exception");
            refuse_independent_presentation("native_loop_exception");
            _render_frame_enabled = false;
            if (!_render_inside_loop) RenderPassContext.IsActive = false;
        }
        if (!_render_inside_loop) return;
        // A draw callback can derive this global from exec zero; do not carry that pause into simulation.
        viewport_restore(RenderPassContext.IsPresentation);
        party_swap_end_pass(RenderPassContext.IsPresentation);
        pad_latch_end_pass();
        render_restore_particles(_render_particle_pause_before);
        _render_particle_pause_before = null;
        double? tickDeadlineSeconds = nativeReturned ? render_simulation_tick_deadline() : null;
        object? padTickCorrelation = nativeReturned ? pad_observations_pass_completed(tickDeadlineSeconds) : null;
        if (nativeReturned && _render_clock_seen && _render_simulation_pass)
        {
            _simulation_ticks++;
            if (_render_steps_remaining > 0) _render_steps_remaining--;
            render_camera_complete_simulation();
            render_actors_complete_simulation();
        }
        else if (nativeReturned && _render_clock_seen) _additional_images++;
        diagnostic_event("render_pass", new { simulation = _render_simulation_pass, clock_seen = _render_clock_seen,
            remaining = _render_steps_remaining, alpha = _render_plan.Alpha, simulation_ticks = _simulation_ticks,
            additional_images = _additional_images, discarded_steps = _simulation_debt_dropped,
            native_clock_owner = !_render_frame_enabled, tick_deadline_seconds = tickDeadlineSeconds,
            pad_tick_correlation = padTickCorrelation });
        if (!nativeReturned) RenderPassContext.IsActive = false;
        RenderPassContext.IsPresentation = false;
        battle_effects_end_pass();
        _render_inside_loop = false;
    }

    private double? render_simulation_tick_deadline()
        => (IndependentPresentationEnabled || _diagnostics?.IsCapturing == true) && _render_clock_seen && _render_simulation_pass
            && OwnsRenderPass && _presentationAdmission.Failure is null
            && _simulationClock.TryGetStepDeadline(_render_plan.Steps - _render_steps_remaining, out double deadline)
            ? deadline : null;

    private RenderClockAction render_scheduler_clock(float nativeDelta)
    {
        if (!IsOwnedRenderLoop) return RenderClockAction.Native;
        _render_clock_seen = true;
        double speed = _render_input_delta > 0 && float.IsFinite(nativeDelta) ? nativeDelta / _render_input_delta : 1;
        _render_clock_speed = double.IsFinite(speed) ? Math.Clamp(speed, 0, 8) : 1;
        bool nativeInputOwner = _render_frame_enabled && pad_native_input_owner();
        if (nativeInputOwner && (RenderPassContext.IsPresentation || _padSnapshotHeld || _padReplayLoan is not null))
        {
            _presentationAdmission.Refuse("render_clock_native_owner_after_presentation");
            return RenderClockAction.Presentation;
        }
        bool snapshotRejected = _render_frame_enabled && _render_snapshot_ready_at_frame
            && !pad_snapshot_presentation_ready(allowNativeInputOwner: nativeInputOwner);
        if (_render_frame_enabled && (snapshotRejected || !render_scheduler_frame_eligible(allowNativeInputOwner: nativeInputOwner)))
        {
            _presentationAdmission.Refuse(snapshotRejected ? "pad_snapshot_clock_readiness_lost" : "render_clock_readiness_lost");
            _render_frame_enabled = false;
            _render_plan_pending = false;
            RenderPassContext.IsActive = false;
            RenderPassContext.SetPass(1, false);
            render_camera_set_presentation_alpha(1);
            RenderPassContext.ResetHistory();
            reset_render_camera();
            reset_render_actors();
        }
        else if (nativeInputOwner)
        {

            render_scheduler_native_input_handover();
        }
        if (_render_plan_pending)
        {
            if (!_render_frame_enabled)
            {
                _simulationClock.Reset();
                _render_plan = new(1, 1, !_render_snapshot_warmup, 0);
            }
            else _render_plan = _simulationClock.Plan(_render_now, _render_clock_speed);
            _render_plan_pending = false;
            _render_steps_remaining = _render_plan.Steps;
            _simulation_debt_dropped += _render_plan.DroppedSteps;
            if (_render_plan.Reset)
            {
                RenderPassContext.ResetHistory();
                reset_render_camera();
                reset_render_actors();
            }
        }
        if (_render_frame_enabled && _render_steps_remaining == 0 && !pad_snapshot_presentation_ready())
        {
            _presentationAdmission.Refuse("pad_snapshot_presentation_without_baseline");
            _render_frame_enabled = false;
            RenderPassContext.IsActive = false;
        }
        _render_simulation_pass = !_render_frame_enabled || _render_steps_remaining > 0;
        RenderPassContext.IsPresentation = !_render_simulation_pass;
        if (RenderPassContext.IsPresentation) viewport_presentation_begin();
        pad_observations_prepare_tick(render_simulation_tick_deadline());
        pad_latch_pass_decided(RenderPassContext.IsPresentation);
        battle_effects_begin_pass(RenderPassContext.IsPresentation);
        RenderPassContext.SetPass(_render_frame_enabled ? _render_plan.Alpha : 1, _render_frame_enabled && (!_render_simulation_pass || _render_steps_remaining <= 1));
        render_camera_set_presentation_alpha(!_render_frame_enabled || _render_steps_remaining > 1 ? 1 : _render_plan.Alpha);
        if (_render_simulation_pass)
        {
            RenderPassContext.BeginSimulation();
            render_camera_begin_simulation();
            render_actors_begin_simulation();
        }
        else
        {
            _advance_frame = false;
            _render_particle_pause_before = render_pause_particles();
            render_camera_begin_presentation();
            render_actors_begin_presentation(_render_plan.Alpha);
        }
        if (!_render_frame_enabled) return RenderClockAction.Native;
        if (_render_simulation_pass)
        {
            decide_frame_advance();
            advance_overlay_spawn_clock();
            pad_native_clock_invoke(1f / TargetFramerate);
            // The native clock can select syncdata while fetching its next record.
            if (pad_native_input_owner() || _padPublicationNativeFallback)
            {
                render_scheduler_native_input_handover();

                return RenderClockAction.Advanced;
            }
        }
        // The runner consumes this value after the clock; intermediate catch-up passes omit draw.
        FhUtil.set_at(EngineAddresses.ElapsedFrameCountFrameSkip, Math.Max(1, _render_steps_remaining));
        return _render_simulation_pass ? RenderClockAction.Advanced : RenderClockAction.Presentation;
    }

    private void render_scheduler_native_input_handover()
    {
        _render_frame_enabled = _render_plan_pending = false;
        RenderPassContext.IsActive = false;
        RenderPassContext.SetPass(1, false);
        _simulationClock.Reset();
        _render_plan = new(1, 1, true, 0);
        _render_steps_remaining = 1;
        render_camera_set_presentation_alpha(1);
        RenderPassContext.ResetHistory();
        reset_render_camera();
        reset_render_actors();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint d_render_exec();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_render_void();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_render_emit(nint manager);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_render_exec()
    {
        if (!RuntimeEffectsArmed)
            return HookSites.Native(() => new FhCallHandle<d_render_exec>(new FhCallLocation(RenderGetExec, 0)).chain_from(h_render_exec).fnptr)();

        uint native = new FhCallHandle<d_render_exec>(new FhCallLocation(RenderGetExec, 0)).chain_from(h_render_exec).fnptr!();
        if (pad_snapshot_exec_must_stay_zero()) return 0;
        uint effective = OwnsRenderPass
            ? FixedSimulationClock.ResolveExec(native, RenderPassContext.IsPresentation) : native;
        pad_snapshot_exec_completed(effective);
        return effective;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_render_calc_rate()
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_void>(new FhCallLocation(RenderCalcRate, 0)).chain_from(h_render_calc_rate).fnptr)();
            return;
        }

        new FhCallHandle<d_render_void>(new FhCallLocation(RenderCalcRate, 0)).chain_from(h_render_calc_rate).fnptr!();
        if (!IsOwnedPresentationPass) return;
        FhUtil.set_at(RenderRateInteger, 0);
        FhUtil.set_at(RenderRateFloat, 0f);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_render_particle_emit(nint manager)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_render_emit>(new FhCallLocation(RenderParticleEmit, 0)).chain_from(h_render_particle_emit).fnptr)(manager);
            return;
        }

        if (!IsOwnedPresentationPass)
            new FhCallHandle<d_render_emit>(new FhCallLocation(RenderParticleEmit, 0)).chain_from(h_render_particle_emit).fnptr!(manager);
    }

    private int? render_pause_particles()
    {
        if (!IsOwnedPresentationPass) return null;
        int previous = FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF);
        FhUtil.set_at(EngineAddresses.PpvUserStopPartF, 1);
        return previous;
    }

    private void render_restore_particles(int? previous)
    {
        if (previous is { } saved && FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) == 1)
            FhUtil.set_at(EngineAddresses.PpvUserStopPartF, saved);
    }
}
