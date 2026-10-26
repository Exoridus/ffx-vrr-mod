namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Retiming of the systems that express a duration in frames. Each of them takes a frame count or
///     a speed as an argument, so the correction is a scale applied on the way in.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     How much longer a frame-expressed duration must be at the current framerate.
    ///
    ///     One is the answer while the engine is pacing itself from the syncdata table. Sg_MainCalcRate
    ///     takes sg_rate from g_sgSyncRate whenever g_isNeedSync is set, and the catch-up loop then
    ///     holds the simulation to the recorded PS2 frame times - the scene already runs at its
    ///     authored rate, and correcting it a second time is what halves it.
    /// </summary>
    private static float Scale => IsSyncPaced ? 1f : TargetFramerate / 30f;

    private static bool IsSyncPaced => _sync_aware && EngineSyncPaced;

    /// <summary>
    ///     The engine's syncdata flag on its own, with the module's opt-out left out of it.
    ///
    ///     The two are not interchangeable. <see cref="VrrConfig.SyncDataAware"/> decides only
    ///     whether this module refrains from scaling such a scene; it has no influence on the engine,
    ///     which throttles its own presentation to the recorded frame times either way. Anything
    ///     reasoning about how fast frames are actually arriving has to read this one, or it stops
    ///     seeing the throttle the moment someone turns the exemption off.
    /// </summary>
    private static bool EngineSyncPaced
        => FhUtil.get_at<uint>(EngineAddresses.IsNeedSync) == 1;

    // Static because Scale is, and Scale is read from static helpers on hot paths.
    private static bool _sync_aware = true;

    /// <summary>
    ///     Frame counts are stored in 16 bits by every consumer this module scales for - the camera
    ///     track record, the fade, flash and alpha slots. Both overloads clamp, because an unclamped
    ///     scale that wrapped would make a long duration a short one rather than a wrong one.
    /// </summary>
    private static ushort scale_up(ushort frames) => (ushort)Math.Min(short.MaxValue, (int)(frames * Scale));
    private static uint scale_up(uint frames) => (uint)Math.Min(short.MaxValue, (long)(frames * Scale));
    /// <summary>
    ///     Halves a speed, never to zero. A motion speed of 0 does not advance at all, so rounding a
    ///     small non-zero speed away stalls whatever waits for that motion to finish - which is how
    ///     a cutscene ends up hanging rather than merely running at the wrong rate.
    /// </summary>
    private static ushort scale_down(ushort speed)
        => speed == 0 ? (ushort)0 : (ushort)Math.Max(1, (int)(speed / Scale));

    private bool init_timing_hooks()
    {
        bool ok = true;

        // The one correction everything else compensates for: with the counters left alone every
        // animation runs at double speed, because the engine derives its animation rate from their
        // delta. It has a switch anyway, because it is also the only baseline against which the
        // other corrections can be told apart - with this off at 60 Hz, anything still mistimed is
        // mistimed for a reason of its own.
        if (_config.VBlankRate)
        {
            ok &= hook_or_log(HookSiteId.TimingVerticalBlankCounters, "vertical blank counters", EngineAddresses.AdvanceVBlankCounters,
                () => new FhCallHandle<d_advance_vblank>(new FhCallLocation(EngineAddresses.AdvanceVBlankCounters, 0)).hook(this, h_advance_vblank));
        }

        if (_config.EventClockOnVblank)
        {
            ok &= hook_or_log(HookSiteId.TimingYiAnimInfoInit, "yiAnimInfo_init", EngineAddresses.YiAnimInfoInit,
                () => new FhCallHandle<d_yi_anim_info_init>(new FhCallLocation(EngineAddresses.YiAnimInfoInit, 0)).hook(this, h_yi_anim_info_init));
        }

        if (_config.SyncDataDisable)
        {
            ok &= hook_or_log(HookSiteId.TimingISyncGetData, "iSyncGetData", EngineAddresses.ISyncGetData,
                () => new FhCallHandle<d_isync_get_data>(new FhCallLocation(EngineAddresses.ISyncGetData, 0)).hook(this, h_isync_get_data));

            _logger.Info("[Vrr] Recorded cutscene pacing refused: the three syncdata scenes will run at the " +
                         "corrected rate and lose the timing their recording asks for.");
        }

        if (_config.Buoyancy) ok &= init_buoyancy_hook();

        // Capture must remain available when both corrections are disabled for a baseline run.
        if (_config.CharacterDelta || _config.Buoyancy || _binaryDiagnostics is not null)
        {
            ok &= hook_or_log(HookSiteId.TimingChCalcMain, "Ch_CalcMain", EngineAddresses.ChCalcMain,
                () => new FhCallHandle<d_ch_calc_main>(new FhCallLocation(EngineAddresses.ChCalcMain, 0)).hook(this, h_ch_calc_main));
        }

        if (_config.AtelWaits)
        {
            bool waitInit = hook_or_log(HookSiteId.TimingATELWaitInit, "ATEL wait init", EngineAddresses.AtelWaitInit,
                () => new FhCallHandle<d_atel_wait_init>(new FhCallLocation(EngineAddresses.AtelWaitInit, 0)).hook(this, h_atel_wait_init));
            bool waitExec = hook_or_log(HookSiteId.TimingATELWaitExec, "ATEL wait exec", EngineAddresses.AtelWaitExec,
                () => new FhCallHandle<d_atel_wait_exec>(new FhCallLocation(EngineAddresses.AtelWaitExec, 0)).hook(this, h_atel_wait_exec));
            bool movieDelta = hook_or_log(HookSiteId.TimingMovieFrameDelta, "movie frame delta", EngineAddresses.MovieFrameDelta,
                () => new FhCallHandle<d_movie_frame_delta>(new FhCallLocation(EngineAddresses.MovieFrameDelta, 0)).hook(this, h_movie_frame_delta));
            _atel_wait_hooks_ready = waitInit && waitExec && movieDelta;
            ok &= _atel_wait_hooks_ready;
        }

        if (_config.Camera)
        {
            ok &= hook_or_log(HookSiteId.TimingMsCameraMoveFrame, "MsCameraMoveFrame", EngineAddresses.MsCameraMoveFrame,
                () => new FhCallHandle<d_camera_move_frame>(new FhCallLocation(EngineAddresses.MsCameraMoveFrame, 0)).hook(this, h_camera_move_frame));
            ok &= hook_or_log(HookSiteId.TimingMsCameraMoveAcc, "MsCameraMoveAcc", EngineAddresses.MsCameraMoveAcc,
                () => new FhCallHandle<d_camera_move_acc>(new FhCallLocation(EngineAddresses.MsCameraMoveAcc, 0)).hook(this, h_camera_move_acc));
        }

        if (_config.Fades)
        {
            ok &= hook_or_log(HookSiteId.TimingSgFadeCommon, "Sg_Fade_Common", EngineAddresses.SgFadeCommon,
                () => new FhCallHandle<d_fade_common>(new FhCallLocation(EngineAddresses.SgFadeCommon, 0)).hook(this, h_fade_common));
            ok &= hook_or_log(HookSiteId.TimingSgFlash, "Sg_Flash", EngineAddresses.SgFlash,
                () => new FhCallHandle<d_sg_flash>(new FhCallLocation(EngineAddresses.SgFlash, 0)).hook(this, h_sg_flash));
            ok &= hook_or_log(HookSiteId.TimingTkSetFadeOut, "TkSetFadeOut", EngineAddresses.TkSetFadeOut,
                () => new FhCallHandle<d_tk_set_fade_out>(new FhCallLocation(EngineAddresses.TkSetFadeOut, 0)).hook(this, h_tk_set_fade_out));
        }

        // The cross-fade setter is the only place the slot 0 hold can see the engine re-entering
        // Sg_AccSetAlpha, so it is installed whenever either of the two corrections is on. With
        // CrossFadeHold set it stops scaling and only carries that flag, which is why the hold
        // reads the result rather than assuming the install took.
        if (_config.Fades || _config.CrossFadeHold)
        {
            _acc_set_alpha_hooked = hook_or_log(HookSiteId.TimingSgAccSetAlpha, "Sg_AccSetAlpha", EngineAddresses.SgAccSetAlpha,
                () => new FhCallHandle<d_acc_set_alpha>(new FhCallLocation(EngineAddresses.SgAccSetAlpha, 0)).hook(this, h_acc_set_alpha));
            ok &= _acc_set_alpha_hooked;
        }

        if (_config.Motion)
        {
            ok &= hook_or_log(HookSiteId.TimingChSetMotionSpeed, "Ch_SetMotionSpeed", EngineAddresses.ChSetMotionSpeed,
                () => new FhCallHandle<d_set_motion_speed>(new FhCallLocation(EngineAddresses.ChSetMotionSpeed, 0)).hook(this, h_set_motion_speed));
            ok &= hook_or_log(HookSiteId.TimingMsEffectSetSpeed, "MsEffectSetSpeed", EngineAddresses.MsEffectSetSpeed,
                () => new FhCallHandle<d_effect_set_speed>(new FhCallLocation(EngineAddresses.MsEffectSetSpeed, 0)).hook(this, h_effect_set_speed));
            ok &= hook_or_log(HookSiteId.TimingMsSetChrMotionParamF, "MsSetChrMotionParamF", EngineAddresses.SetMotionParamFloat,
                () => new FhCallHandle<d_set_motion_param>(new FhCallLocation(EngineAddresses.SetMotionParamFloat, 0)).hook(this, h_set_motion_param));
            ok &= hook_or_log(HookSiteId.TimingChTextureSetAnimTimer, "Ch_TextureSetAnimTimer", EngineAddresses.ChTextureSetAnimTimer,
                () => new FhCallHandle<d_set_tex_anim_timer>(new FhCallLocation(EngineAddresses.ChTextureSetAnimTimer, 0)).hook(this, h_set_tex_anim_timer));
            _logger.Info("[Vrr] MsSetChrStatInfo observation disabled: the current executable target is unresolved.");
        }

        return ok;
    }

    // --- Delegates ---

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ch_calc_main(float delta);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_camera_move_frame(uint camera_id, uint arg2, uint arg3, uint frames, uint arg5);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_camera_move_acc(uint camera_id, uint mode_non_ref, uint mode_polar, uint a4, uint a5, uint a6, uint a7);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_fade_common(ushort frames, uint mode_in, uint mode_w);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_sg_flash(ushort frames, byte arg2, byte arg3, byte arg4);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_acc_set_alpha(ushort alpha, ushort frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_tk_set_fade_out(uint frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_set_motion_speed(nint ptr_actor, ushort speed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_effect_set_speed(byte chr_id, int speed);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_set_motion_param(uint chr_id, uint stat_id, float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_set_chr_stat(uint chr_id, uint stat_id, uint target_id, uint value);

    /* Cdecl, measured rather than taken from the catalog: Ch_TextureSetAnimTimer ends in a plain c3,
     * so the caller cleans. Declaring it StdCall made the detour pop eight bytes the four call sites
     * pop as well, which drifts the stack on every call and faults far from the cause. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_set_tex_anim_timer(nint ptr_chr, int timer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_yi_anim_info_init(nint info);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_isync_get_data(float* total, float* next, int* rate);

    /// <summary>
    ///     Answers the engine's own "no recording for this scene" value without asking the sync
    ///     manager. The three out-parameters are left untouched on purpose: FUN_00821E80 overwrites
    ///     all of them on the 999 path before anything reads them.
    /// </summary>
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_isync_get_data(float* total, float* next, int* rate)
    {
        _sync_refused++;
        return 999;
    }

    private long _sync_refused;

    // --- Handlers ---

    /* yiAnimInfo_init reads KEEP_FPS once and stores it as the clock the event worker's animation
     * curves run on: zero means yiGetFCount, which is sg_count * 2 and gains two units per presented
     * frame with nothing correcting it, non-zero means yiGetVCount, which h_advance_vblank rescales.
     *
     * KEEP_FPS is observed toggling during play, so which clock a scene gets is otherwise decided by
     * whatever it happened to be at init. Writing the mode after the original has run puts every
     * scene on the counter that is corrected. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_yi_anim_info_init(nint info)
    {
        new FhCallHandle<d_yi_anim_info_init>(new FhCallLocation(EngineAddresses.YiAnimInfoInit, 0))
            .chain_from(h_yi_anim_info_init).fnptr?.Invoke(info);

        FhUtil.set_at(EngineAddresses.YiAnimClockMode, 1u);
        _logger.Info("[Vrr] Event worker clock forced to the vertical blank counter.");
    }

    /* The engine always passes a fixed 0.033373334, one 30 Hz frame.
     *
     * Derived from Scale rather than from the target rate, because this is the one correction whose
     * denominator is a count of main loop passes rather than of presented frames. updateFFX repeats
     * the whole Sg_MainLoop body while gElapsedFrameCount_frameSkip is non-zero, and during a
     * syncdata catch-up each of those passes replays one recorded 30 Hz step. Handing it 1/60 there
     * runs character motion at half speed. Scale is 1 in that state, so 1/(30*Scale) gives 1/30 for
     * the catch-up and 1/60 for ordinary play.
     *
     * The delta the engine passed is forwarded unchanged when only the buoyancy hold asked for this
     * hook, so turning the delta rewrite off leaves the argument alone instead of silently keeping
     * the correction it was turned off to remove. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ch_calc_main(float delta)
    {
        var original = new FhCallHandle<d_ch_calc_main>(
            new FhCallLocation(EngineAddresses.ChCalcMain, 0)).chain_from(h_ch_calc_main);

        if (_diagnostics?.IsCapturing == true) diagnostic_event("character_enter", new { authored_delta = delta, applied_delta = _config.CharacterDelta ? 1f / (30f * Scale) : delta });
        diagnostic_raw(6, delta);
        diagnostic_memory(false);
        buoyancy_hold_begin();
        original.fnptr!(_config.CharacterDelta ? 1f / (30f * Scale) : delta);
        // Paused native calls skip the barrier and write no buoyancy state.
        _buoyancy_saved_count = 0;
        diagnostic_raw(7, delta);
        diagnostic_memory(true);
        diagnostic_event("character_exit" );
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_camera_move_frame(uint camera_id, uint arg2, uint arg3, uint frames, uint arg5)
    {
        new FhCallHandle<d_camera_move_frame>(new FhCallLocation(EngineAddresses.MsCameraMoveFrame, 0))
            .chain_from(h_camera_move_frame).fnptr!(camera_id, arg2, arg3, scale_up(frames), arg5);
    }

    /* Settled 2026-09-12; the note that used to stand here said it was not established and followed
     * PWarp on trust. Three of the four are provably frame counts: MsCameraMoveAcc stores the fourth,
     * fifth and sixth arguments as the three segments of the move and writes their sum into the
     * total the camera is stepped against, so the engine itself treats them as a duration split
     * three ways. Scaling them individually keeps the sum consistent, which is why the total is not
     * scaled as well.
     *
     * The seventh is the one that was in doubt and it is scaled for a measured reason rather than a
     * borrowed one. It is stored beside the three segments and its reader is not locatable in the
     * snapshot, but over 70,168 camMoveAcc [6014h] sites it is zero in every authored pattern except
     * the two that dominate the battle scripts - 0/0/80/40 and 0/0/30/15 - where it is exactly half
     * the third segment. A quantity that is authored as half of a frame count is a frame count, and
     * scaling three of the four would break that ratio where scaling all four preserves it. */
    private long _camera_acc_calls;
    private long _motion_speed_calls;

    private string motion_speed_counts()
        => $"mspeed={_motion_speed_calls}";

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_camera_move_acc(uint camera_id, uint mode_non_ref, uint mode_polar, uint a4, uint a5, uint a6, uint a7)
    {
        _camera_acc_calls++;

        // The encounter scripts call camMoveAcc(0, 0, 30, 15) in 540 of 641 CamEnter entry points,
        // so a logged call with those arguments unscaled says the hook fires and the scaling does
        // not, and no call at all says the hook is not on the path. Nothing else distinguishes the
        // two, and the battle entry camera is the one place a player notices.
        if (_config.SurveyCamera && _camera_acc_calls <= 8)
            _logger.Info($"[Vrr] MsCameraMoveAcc #{_camera_acc_calls}: cam={camera_id} p2={mode_non_ref} " +
                         $"p3={mode_polar} in={a4},{a5},{a6},{a7} -> out={scale_up(a4)},{scale_up(a5)}," +
                         $"{scale_up(a6)},{scale_up(a7)} (scale={Scale:F2})");

        new FhCallHandle<d_camera_move_acc>(new FhCallLocation(EngineAddresses.MsCameraMoveAcc, 0))
            .chain_from(h_camera_move_acc).fnptr!(camera_id, mode_non_ref, mode_polar,
                scale_up(a4), scale_up(a5), scale_up(a6), scale_up(a7));
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_fade_common(ushort frames, uint mode_in, uint mode_w)
    {
        new FhCallHandle<d_fade_common>(new FhCallLocation(EngineAddresses.SgFadeCommon, 0))
            .chain_from(h_fade_common).fnptr!(scale_up(frames), mode_in, mode_w);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_sg_flash(ushort frames, byte arg2, byte arg3, byte arg4)
    {
        new FhCallHandle<d_sg_flash>(new FhCallLocation(EngineAddresses.SgFlash, 0))
            .chain_from(h_sg_flash).fnptr!(scale_up(frames), arg2, arg3, arg4);
    }

    /* The frame count goes through untouched under CrossFadeHold: the slot it seeds is stepped by a
     * clamped integer, so the correction is the hold in VrrModule.CrossFade and scaling here as
     * well would slow the ramp twice. The flag it raises is read by that hold and by nothing else. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_acc_set_alpha(ushort alpha, ushort frames)
    {
        _acc_set_alpha_reentered = true;

        new FhCallHandle<d_acc_set_alpha>(new FhCallLocation(EngineAddresses.SgAccSetAlpha, 0))
            .chain_from(h_acc_set_alpha).fnptr!(alpha, _cross_fade_hold_active ? frames : scale_up(frames));
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_tk_set_fade_out(uint frames)
    {
        new FhCallHandle<d_tk_set_fade_out>(new FhCallLocation(EngineAddresses.TkSetFadeOut, 0))
            .chain_from(h_tk_set_fade_out).fnptr!(scale_up(frames));
    }

    /* Passes the authored speed through. A rate correction must not be written here, and the reason
     * is the field rather than the arithmetic.
     *
     * Ch_SetMotionSpeed writes actor+0x750, and nothing resets it: Ch_SetMotionSeq rewrites the
     * per-motion multiplier at +0x754 on every motion change and leaves +0x750 alone, so a value
     * written once outlives the motion, the scene and the flag state that justified it. The engine
     * decides whether to rate-correct an actor from the global KEEP_FPS and the actor's own bit
     * 0x100000, and it decides that per call, from state a script can change at any time. Encoding
     * that decision into a persistent field means the module commits to an answer that the engine
     * is free to contradict a few instructions later, and it then corrects the same actor twice for
     * the rest of the scene. No gate on the state at write time can avoid it, because the state
     * being gated on is not the state that will be true when the value is read.
     *
     * The correction therefore lives at the advance, where the same decision is taken per call and
     * leaves nothing behind. See VrrModule.MotionSurvey. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_set_motion_speed(nint ptr_actor, ushort speed)
    {
        // How many motion speeds this module ever sees is the coverage question behind "some
        // animations are still fast": an actor whose speed is set from loaded data or left at its
        // default never passes through here.
        _motion_speed_calls++;

        new FhCallHandle<d_set_motion_speed>(new FhCallLocation(EngineAddresses.ChSetMotionSpeed, 0))
            .chain_from(h_set_motion_speed).fnptr!(ptr_actor, speed);
    }

    /* A negative speed is not a speed. MsEffectSetSpeed treats it as "drop the override": it clears
     * the actor's effect speed field and hands the decision back to MsCalcMotionSpeed. Reading the
     * argument as unsigned turned that sentinel into 0x7FFF, so a reset became the fastest effect
     * speed the field can hold. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_effect_set_speed(byte chr_id, int speed)
    {
        if (speed >= 0) speed = scale_down((ushort)Math.Min(ushort.MaxValue, speed));

        new FhCallHandle<d_effect_set_speed>(new FhCallLocation(EngineAddresses.MsEffectSetSpeed, 0))
            .chain_from(h_effect_set_speed).fnptr!(chr_id, speed);
    }

    /* Only the four run-speed properties are frame-expressed. Ids 0, 1, 2, 7 and 8 are distances,
     * offsets and a weight; scaling those would move characters, not retime them.
     *
     * Id 6 is not a speed but an acceleration, and it therefore scales with the square. The consumer
     * integrates it once per frame - v += motion_run_speed_acc, then pos -= v, starting from
     * motion_run_speed_v0 (id 5). Under t -> 2t a per-frame velocity halves and a per-frame velocity
     * increment quarters; treating both alike left the acceleration twice as strong as the rest of
     * the motion, which reads as a character that starts correctly and then overshoots. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_set_motion_param(uint chr_id, uint stat_id, float value)
    {
        float scaled = stat_id switch
        {
            3 or 4 or 5 => value / Scale,
            6           => value / (Scale * Scale),
            _           => value
        };

        new FhCallHandle<d_set_motion_param>(new FhCallLocation(EngineAddresses.SetMotionParamFloat, 0))
            .chain_from(h_set_motion_param).fnptr!(chr_id, stat_id, scaled);
    }

    /* Passes every stat through unchanged. The hook stays because the address and the reasoning are
     * worth keeping in one place, and re-enabling a correction here is one line.
     *
     * Both corrections this used to make were wrong, and for opposite reasons.
     *
     * STAT_ATTACK_INC_SPEED and _DEC_SPEED are not rates. The engine's own debug dump names them
     * 攻撃：加速％ and 減速％ - percentages, stored as single bytes at Chr+0x4B9 and +0x4BA, and no
     * function in the image reads either one.
     *
     * The three frame-count stats are durations, but scaling them alone makes characters travel
     * further rather than slower. Chr+0x40C is the iteration count of a per-frame integration whose
     * result is a distance: the loop adds Chr+0x468 into a velocity and the velocity into a
     * position, and those three rates arrive as ATEL float arguments that nothing here scales.
     * Doubling only the loop count doubles the distance walked. They are also bytes, so a scaled
     * value above 127 wrapped. The correction belongs at the ATEL opcode that sets the rates, with
     * the frame counts, or nowhere. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_set_chr_stat(uint chr_id, uint stat_id, uint target_id, uint value)
    {
        new FhCallHandle<d_set_chr_stat>(new FhCallLocation(EngineAddresses.MsSetChrStatInfo, 0))
            .chain_from(h_set_chr_stat).fnptr!(chr_id, stat_id, target_id, value);
    }

    /* Passes the value through. It is not a period.
     *
     * The byte this writes is the step the new-format advance adds to its sequence accumulator each
     * call - the accumulator is then compared against the per-sprite duration, so the period lives
     * in the asset and the timer is the rate. Scaling it up therefore doubled a rate that was
     * already running twice too fast, which is 4x rather than a correction. Its only two engine
     * writers confirm the reading: MsCalcMotionSpeed writes the literals 1 and 0 on the zero
     * crossing of the motion speed, so it is pause and resume, not a duration.
     *
     * The correction the byte can express is a hold, not a factor - write 0 on a skipped frame and
     * restore the engine's own last value on an advancing one. That needs per-slot state this hook
     * does not have yet, and until it does, running vanilla is the correct behaviour rather than a
     * placeholder. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_set_tex_anim_timer(nint ptr_chr, int timer)
    {
        new FhCallHandle<d_set_tex_anim_timer>(new FhCallLocation(EngineAddresses.ChTextureSetAnimTimer, 0))
            .chain_from(h_set_tex_anim_timer).fnptr!(ptr_chr, timer);
    }
}
