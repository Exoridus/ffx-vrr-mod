namespace Fahrenheit.Mods.FpsUnlock;

/// <summary>
///     Systems that play a sequence of prepared images, one per frame, and carry no notion of a
///     duration to scale. They can only be held: the same image is presented for as many frames as
///     the framerate multiplier demands.
///
///     This is frame skipping, and it means these subsystems remain 30 Hz inside a 60 Hz game. It is
///     a deliberate, temporary exception, taken because the alternative is a visibly wrong playback
///     speed. The real fix is content that matches the target framerate.
/// </summary>
public unsafe sealed partial class FpsUnlockModule
{
    /// <summary>
    ///     Decided once per presented frame and read many times, by every hold in the module. A
    ///     modulo would tie the holds to whole multiples of 30; the carry below does not, so a
    ///     framerate that is not a multiple - 75 Hz, or 90 - gets an uneven but correct pattern
    ///     instead of a rounded and wrong one.
    /// </summary>
    private bool _advance_frame = true;
    private double _hold_carry;

    private void decide_frame_advance()
    {
        _hold_carry += 1.0 / Math.Max(1f, Scale);

        if (_hold_carry >= 1.0) { _hold_carry -= 1.0; _advance_frame = true; }
        else                    { _advance_frame = false; }
    }

    /// <summary>True on the frames a held sequence is allowed to advance.</summary>
    private bool advance_this_frame() => _advance_frame;

    /// <summary>
    ///     The same decision, but per call instead of per presented frame, and deliberately blind to
    ///     the syncdata pacing.
    ///
    ///     A video consumes exactly one decoded picture per call and derives its own clock from how
    ///     many it has consumed, so what has to be halved is the call, not the frame. Gating on the
    ///     frame assumes one call per frame; where the engine calls twice - the render section runs
    ///     max(1, Sg_GetCurExecFrames) times - both calls land on an allowed frame and the video
    ///     runs at full speed anyway.
    ///
    ///     And the syncdata exemption must not reach it. That exemption exists because the engine
    ///     paces itself through sg_rate in those scenes; a video hangs on the presentation rate
    ///     instead, so treating a sync-paced scene as needing no correction silently switches the
    ///     hold off exactly where a cutscene is playing.
    /// </summary>
    private bool advance_this_call(ref double carry)
    {
        float scale = Math.Max(1f, TargetFramerate / 30f);

        carry += 1.0 / scale;
        if (carry < 1.0) return false;

        carry -= 1.0;
        return true;
    }

    private bool init_frame_sequence_hooks()
    {
        bool ok = true;

        if (_config.MenuWater)
        {
            ok &= hook_or_log("graphicDrawMainMenuWaterEffect", EngineAddresses.GraphicDrawMainMenuWaterEffect,
                () => new FhMethodHandle<d_menu_water>(new FhMethodLocation(EngineAddresses.GraphicDrawMainMenuWaterEffect, 0)).hook(this, h_menu_water));
        }

        if (_config.Fmv)
        {
            ok &= hook_or_log("graphicVideoUpdate", EngineAddresses.GraphicVideoUpdate,
                () => new FhMethodHandle<d_video_update>(new FhMethodLocation(EngineAddresses.GraphicVideoUpdate, 0)).hook(this, h_video_update));
        }

        if (_config.TextureVideo)
        {
            ok &= hook_or_log("graphicTextureVideoUpdate", EngineAddresses.GraphicTextureVideoUpdate,
                () => new FhMethodHandle<d_texture_video_update>(new FhMethodLocation(EngineAddresses.GraphicTextureVideoUpdate, 0)).hook(this, h_texture_video_update));
        }

        if (_config.BattleIntroBlur)
        {
            ok &= hook_or_log("battle entry blur", EngineAddresses.EternalBlurTransition,
                () => new FhMethodHandle<d_eternal_blur>(new FhMethodLocation(EngineAddresses.EternalBlurTransition, 0)).hook(this, h_eternal_blur));
        }

        if (_config.TextureAnimation)
        {
            ok &= hook_or_log("old-format texture animation advance", EngineAddresses.ChrTexAnimAdvanceOld,
                () => new FhMethodHandle<d_texanim_advance_old>(new FhMethodLocation(EngineAddresses.ChrTexAnimAdvanceOld, 0)).hook(this, h_texanim_advance_old));
        }

        return ok;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_menu_water();

    /* Cdecl by default rather than by evidence: graphicVideoUpdate ends in a jmp, so it hands its
     * frame to the target and nothing here states who cleans. It takes no arguments, so the two
     * conventions are identical for it and the choice does not matter - unlike setMaterialUVScroll,
     * which really does pop its own. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_video_update();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texanim_advance_old(int slot);

    /* No arguments, so the convention carries no risk either way. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texture_video_update();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_eternal_blur(int arg1, int arg2);

    private long _texture_video_calls;
    private long _texture_video_held;
    private double _texture_video_carry;
    private long _blur_transitions;

    private string frame_sequence_counts()
        => $"texvid={_texture_video_calls}/{_texture_video_held} fmv={_video_calls}/{_video_held} " +
           $"blur_set={_blur_transitions}";

    /* Texture videos are not FMVs and the FMV frameskip does not reach them - Zanarkand coming to
     * life in Dream's End is one. The update runs once per main loop iteration and takes no time
     * step, so like every prepared sequence in this file it can only be held. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texture_video_update()
    {
        _texture_video_calls++;

        if (!advance_this_call(ref _texture_video_carry)) { _texture_video_held++; return; }

        new FhMethodHandle<d_texture_video_update>(new FhMethodLocation(EngineAddresses.GraphicTextureVideoUpdate, 0))
            .chain_from(h_texture_video_update).fnptr?.Invoke();
    }

    /* The eternal effect VM's transition blur writes force_wait_blur_frame_count = 0x5a, and the
     * battle load sequencer counts it down once per presented frame before it will start the
     * encounter. Scaling the count after the original has written it is the whole correction; the
     * sequencer itself must not be held, because it is a state machine with load steps rather than
     * a timer. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_eternal_blur(int arg1, int arg2)
    {
        new FhMethodHandle<d_eternal_blur>(new FhMethodLocation(EngineAddresses.EternalBlurTransition, 0))
            .chain_from(h_eternal_blur).fnptr?.Invoke(arg1, arg2);

        uint frames = FhUtil.get_at<uint>(EngineAddresses.ForceWaitBlurFrameCount);
        if (frames == 0) return;

        FhUtil.set_at(EngineAddresses.ForceWaitBlurFrameCount, scale_up(frames));
        _blur_transitions++;
    }

    /* The main menu water is 69 images composited one per frame, with the current index in a byte.
     * The draw itself advances the index, so the hold is applied afterwards by winding it back on
     * the frames that are not allowed to advance. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_menu_water()
    {
        byte before = FhUtil.get_at<byte>(EngineAddresses.MenuWaterFrame);

        new FhMethodHandle<d_menu_water>(new FhMethodLocation(EngineAddresses.GraphicDrawMainMenuWaterEffect, 0))
            .chain_from(h_menu_water).fnptr!();

        if (!advance_this_frame()) FhUtil.set_at(EngineAddresses.MenuWaterFrame, before);
    }

    /* The video update loop multiplies its time step by a hardcoded 29.97 and ignores the timer it
     * keeps, so the only lever on a 29.97 source is how often the update runs at all.
     *
     * The skip belongs here rather than inside the FMV manager. Holding the manager's own texture
     * update leaves it mid-state and faults during playback, which is what an earlier build did.
     *
     * This is the hook that has to go once real 60 FPS video ships: with a 59.94 asset the update
     * belongs on every frame, and holding it would halve the video's own framerate. */
    private double _video_carry;
    private long _video_calls;
    private long _video_held;

    /// <summary>
    ///     Whether a video is actually playing, which is the same test graphicVideoUpdate makes on
    ///     its first line. Skipping a call while it is false holds nothing - the function returns
    ///     immediately - but it does consume a step of the carry, so the first real video frame
    ///     would arrive on an arbitrary phase and the counters would report main loop iterations
    ///     rather than video frames.
    /// </summary>
    private static bool fmv_playing()
    {
        nint manager = FhUtil.get_at<nint>(EngineAddresses.FmvPlayerManager);
        // The manager pointer is read on every presented frame, including the frames before the
        // manager exists and after it is gone, and the flag behind it has no other guard.
        return MemoryProbe.IsReadable(manager, FmvPlaybackFlagOffset + 1)
            && *(byte*)(manager + FmvPlaybackFlagOffset) != 0;
    }

    private const int FmvPlaybackFlagOffset = 0x6D0;

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_video_update()
    {
        var original = new FhMethodHandle<d_video_update>(new FhMethodLocation(EngineAddresses.GraphicVideoUpdate, 0))
            .chain_from(h_video_update);

        if (!fmv_playing()) { original.fnptr!(); return; }

        _video_calls++;

        if (!advance_this_call(ref _video_carry)) { _video_held++; return; }

        original.fnptr!();
    }

    /* The engine carries two texture animation formats side by side and picks one per character on
     * byte 2 of the descriptor. The new format's advance takes its step from Ch_TextureSetAnimTimer,
     * which this module already retimes; this is the old one, and its counters are literal - the
     * sprite step is +1 per call and the blink countdown is rand() % 0x5a + 0x3c ticked down by one.
     * Nothing in it can be scaled, so the call itself is held instead.
     *
     * 445 of 872 character model directories ship without the new format's asset, so this is not a
     * rare path. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texanim_advance_old(int slot)
    {
        _texanim_advance_old_path++;

        if (!advance_this_frame()) return;

        new FhMethodHandle<d_texanim_advance_old>(new FhMethodLocation(EngineAddresses.ChrTexAnimAdvanceOld, 0))
            .chain_from(h_texanim_advance_old).fnptr?.Invoke(slot);
    }
}
