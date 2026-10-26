namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Particle retiming. Every particle manager keeps a fixed-point time accumulator and advances
///     it by a per-manager step each frame, so unlike the frame-sequence systems particles carry a
///     rate that can be scaled rather than only held.
///
///     Two writers set that step and both have to be caught.
///
///     <c>_pppStartPart</c> writes its second argument into the manager's +0x10 when the manager
///     starts. Scaling the argument there covers every source of an initial step, including the
///     literal 0x1000 the magic path passes, which a write to the global would never reach.
///
///     <c>pppDataRcv</c>, which <c>pppPartLoop</c> calls at its end, broadcasts the global step into
///     every active manager's +0x10 again. That is the PS2 path: measured 2026-08-30 with the loop
///     hook attached, part_rescaled stayed 0 and the global step read 0, so the broadcast does not
///     run on PC and the start scaling is never undone.
///
///     Which leaves the step alone unable to fix the speed at all. <c>_pppRunPart</c> calls
///     <c>pppRunPartStd</c> before, and independently of, the accumulator update, so the
///     instruction pass runs once per call: particle motion follows call frequency, and the step
///     only moves the emission clock and the group's end. The frequency is addressed one level
///     down, per kernel, in <see cref="VrrConfig.ParticleKernelHold"/>.
///
///     Holding at this level was tried and is not available: the same call builds the draw packet,
///     so a held frame drops that manager's contribution and the effect flickers. That is why the
///     step here is scaled by the same integer divisor the object age is patched with, and nothing
///     above the kernels is ever skipped.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const int ParticleManagerStride = 0x80;
    private const int ParticleStepOffset    = 0x10;

    private long _part_starts;
    private long _part_runs;
    private long _fp_runs;
    private readonly Dictionary<nint, (long Frame, long Ticks)> _fp_started = [];
    private readonly Queue<double> _fp_lifetimes = new();
    private readonly Queue<long> _fp_life_frames = new();
    private long _part_loops;
    private long _part_rescaled;
    private int  _part_step_observed;

    private bool init_particle_hooks()
    {
        if (!_config.ParticleTimeline && !IndependentPresentationEnabled && !_config.ParticleSpriteClock) return true;
        if (!init_particle_sprite_clock()) return false;

        bool ok = hook_or_log("_pppStartPart", EngineAddresses.PppStartPart,
            () => new FhCallHandle<d_ppp_start_part>(new FhCallLocation(EngineAddresses.PppStartPart, 0)).hook(this, h_ppp_start_part));
        if (_config.ParticleTimeline)
        {
            ok &= hook_or_log("pppPartLoop", EngineAddresses.PppPartLoop,
                () => new FhCallHandle<d_ppp_part_loop>(new FhCallLocation(EngineAddresses.PppPartLoop, 0)).hook(this, h_ppp_part_loop));
            ok &= hook_or_log("_pppRunPartFp", EngineAddresses.PppRunPartFp,
                () => new FhCallHandle<d_ppp_run_part_fp>(new FhCallLocation(EngineAddresses.PppRunPartFp, 0)).hook(this, h_ppp_run_part_fp));
        }
        ok &= hook_or_log("_pppRunPart", EngineAddresses.PppRunPart,
            () => new FhCallHandle<d_ppp_run_part>(new FhCallLocation(EngineAddresses.PppRunPart, 0)).hook(this, h_ppp_run_part));
        return ok;
    }

    private string particle_counts()
        => $"part_starts={_part_starts} part_runs={_part_runs} part_loops={_part_loops} " +
           $"part_rescaled={_part_rescaled} fp_runs={_fp_runs} {field_lifetimes()} " +
           $"part_step=0x{_part_step_observed:X}";

    private int scale_particle_step(int step)
    {
        // A step of zero is a manager that is not meant to advance; scaling it is meaningless, and
        // rounding a small step to zero would freeze the effect outright. A timeline step of zero
        // is a rate that has not been adopted yet, and there is no divisor to apply.
        if (step <= 0 || _timeline_step <= 0) return step;

        // The emission clock has to move by exactly the factor the object age moved by, and the age
        // is patched with an integer divisor rather than with Scale: the halved age sequence only
        // contains every value the old one reached when the step divides 0x1000 exactly. Deriving
        // the emission factor from Scale instead lets the two drift apart at any rate that is not a
        // whole multiple of 30 - at 50 Hz the age would step by 0x1000/2 while emission slowed by
        // 1.67 - which is the doubled standing population the timeline patch exists to avoid.
        return Math.Max(1, step / (VanillaAgeStep / _timeline_step));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ppp_start_part(nint manager, int time_step, nint data, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ppp_part_loop();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint d_ppp_run_part(nint manager, byte mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint d_ppp_run_part_fp(nint manager, uint mode);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ppp_start_part(nint manager, int time_step, nint data, int flags)
    {
        _fieldDeferral.Drop(manager);
        field_particle_manager_changed(manager);
        if (_particle_sprite_installed) _particle_sprite_clock.ResetManager(manager);
        if (_particle_pilot_installed) reset_particle_presentation();
        _part_starts++;
        _part_step_observed = time_step;
        int applied = _config.ParticleTimeline ? scale_particle_step(time_step) : time_step;
        if (_config.ParticleTimeline) _timeline_manager_steps[manager] = (time_step, applied);

        new FhCallHandle<d_ppp_start_part>(new FhCallLocation(EngineAddresses.PppStartPart, 0))
            .chain_from(h_ppp_start_part).fnptr?.Invoke(manager, applied, data, flags);
    }

    /* Counted, never held. This is the choke point all eight callers pass through, which is what
     * made it the obvious place to skip a frame - and skipping here is exactly what cannot be done:
     * the same call builds the manager's draw packet, so a held frame drops that manager from the
     * image. Retiming happens per kernel instead, one level down. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_ppp_run_part(nint manager, byte mode)
    {
        _part_runs++;

        var orig = new FhCallHandle<d_ppp_run_part>(new FhCallLocation(EngineAddresses.PppRunPart, 0))
            .chain_from(h_ppp_run_part).fnptr;

        int? paused = render_pause_particles();
        try
        {
            if (orig is null) return 0;
            return _particle_pilot_installed ? particle_pilot_run(manager, mode, orig) : orig(manager, mode);
        }
        finally { render_restore_particles(paused); }
    }

    /* The field half, counted for the same reason and held for none. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private uint h_ppp_run_part_fp(nint manager, uint mode)
    {
        _fp_runs++;

        if (!_fp_started.ContainsKey(manager)) note_field_start(manager);

        var orig = new FhCallHandle<d_ppp_run_part_fp>(new FhCallLocation(EngineAddresses.PppRunPartFp, 0))
            .chain_from(h_ppp_run_part_fp).fnptr;

        // Before emission: a deferred free must return its pool slot before this step spawns.
        if (!RenderPassContext.IsPresentation) field_particle_flush_deferred(manager);
        int? paused = render_pause_particles();
        uint result;
        try { result = orig is null ? 0 : orig(manager, mode); }
        finally { render_restore_particles(paused); }

        // Non-zero is pppFpLoop's signal to free the group and start it again.
        if (result != 0) { _fieldDeferral.Drop(manager); note_field_end(manager); }

        return result;
    }

    /* How long a field particle group lives, in wall clock and in presented frames.
     *
     * This is the measurement the eye cannot make. A group's lifetime is authored in the manager's
     * own time units, so at the authored rate it should take the same number of seconds however
     * fast the game presents frames. Twice the advance rate halves the seconds and leaves the frame
     * count alone; a correct retiming restores the seconds and doubles the frames. */
    private string field_lifetimes()
    {
        if (_fp_lifetimes.Count == 0) return "fp_life=-";

        double ms = _fp_lifetimes.Average();
        double frames = _fp_life_frames.Average();

        return $"fp_life={ms:F0}ms/{frames:F0}f(n={_fp_lifetimes.Count})";
    }

    private void note_field_start(nint manager)
    {
        _fp_started[manager] = (_frames, Stopwatch.GetTimestamp());
    }

    private void note_field_end(nint manager)
    {
        if (!_fp_started.Remove(manager, out var start)) return;

        double ms = (Stopwatch.GetTimestamp() - start.Ticks) * 1000.0 / Stopwatch.Frequency;

        // A group that lived a single frame is a restart, not a lifetime.
        if (ms < 16) return;

        _fp_lifetimes.Enqueue(ms);
        _fp_life_frames.Enqueue(_frames - start.Frame);

        while (_fp_lifetimes.Count > 20) { _fp_lifetimes.Dequeue(); _fp_life_frames.Dequeue(); }
    }


    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ppp_part_loop()
    {
        _part_loops++;

        new FhCallHandle<d_ppp_part_loop>(new FhCallLocation(EngineAddresses.PppPartLoop, 0))
            .chain_from(h_ppp_part_loop).fnptr?.Invoke();

        int global_step = FhUtil.get_at<int>(EngineAddresses.PpvPartTimeStep);
        if (global_step <= 0) return;

        int scaled = scale_particle_step(global_step);
        if (scaled == global_step) return;

        int count = FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount);
        byte* managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers);

        for (int i = 0; i < count; i++)
        {
            int* step = (int*)(managers + i * ParticleManagerStride + ParticleStepOffset);
            if (*step != global_step) continue;

            *step = scaled;
            _part_rescaled++;
        }
    }
}
