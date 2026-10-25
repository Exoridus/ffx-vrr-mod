namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Schedules prepared image updates and FMV uploads at their source cadence. Original menu and
///     texture sequences retain discrete authored images; validated replacements supply additional
///     frames. Native movie position depends on consumed frames and the file's frame duration;
///     the authored movie-to-script conversion remains unchanged.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>True when the current simulation pass may advance a held sequence.</summary>
    private bool advance_this_frame() => !RenderPassContext.IsPresentation && _advance_frame;

    /// <summary>
    ///     Samples the original texture-video path at its authored 30 Hz cadence, independently of
    ///     syncdata. FMV upload sampling uses each validated asset's frame rate separately.
    /// </summary>
    private bool advance_this_call(ref double carry)
    {
        if (RenderPassContext.IsPresentation) return false;
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
                () => new FhCallHandle<d_menu_water>(new FhCallLocation(EngineAddresses.GraphicDrawMainMenuWaterEffect, 0)).hook(this, h_menu_water));
        }

        if (_config.Fmv)
        {
            ok &= hook_or_log("graphicVideoUpdate", EngineAddresses.GraphicVideoUpdate,
                () => new FhCallHandle<d_video_update>(new FhCallLocation(EngineAddresses.GraphicVideoUpdate, 0)).hook(this, h_video_update));
        }

        if (_config.TextureVideo)
        {
            ok &= hook_or_log("graphicTextureVideoUpdate", EngineAddresses.GraphicTextureVideoUpdate,
                () => new FhCallHandle<d_texture_video_update>(new FhCallLocation(EngineAddresses.GraphicTextureVideoUpdate, 0)).hook(this, h_texture_video_update));
        }

        if (_config.BattleIntroBlur)
        {
            ok &= hook_or_log("battle entry blur", EngineAddresses.EternalBlurTransition,
                () => new FhCallHandle<d_eternal_blur>(new FhCallLocation(EngineAddresses.EternalBlurTransition, 0)).hook(this, h_eternal_blur));
        }

        if (_config.TextureAnimation)
        {
            ok &= hook_or_log("old-format texture animation advance", EngineAddresses.ChrTexAnimAdvanceOld,
                () => new FhCallHandle<d_texanim_advance_old>(new FhCallLocation(EngineAddresses.ChrTexAnimAdvanceOld, 0)).hook(this, h_texanim_advance_old));
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

        new FhCallHandle<d_texture_video_update>(new FhCallLocation(EngineAddresses.GraphicTextureVideoUpdate, 0))
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
        new FhCallHandle<d_eternal_blur>(new FhCallLocation(EngineAddresses.EternalBlurTransition, 0))
            .chain_from(h_eternal_blur).fnptr?.Invoke(arg1, arg2);

        uint frames = FhUtil.get_at<uint>(EngineAddresses.ForceWaitBlurFrameCount);
        if (frames == 0) return;

        FhUtil.set_at(EngineAddresses.ForceWaitBlurFrameCount, scale_up(frames));
        _blur_transitions++;
    }

    /* The original main menu water is 70 images composited one per frame, with the current index in a byte.
     * The draw itself advances the index, so the hold is applied afterwards by winding it back on
     * the frames that are not allowed to advance. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_menu_water()
    {
        _menu_water_calls++;
        byte before = FhUtil.get_at<byte>(EngineAddresses.MenuWaterFrame);

        new FhCallHandle<d_menu_water>(new FhCallLocation(EngineAddresses.GraphicDrawMainMenuWaterEffect, 0))
            .chain_from(h_menu_water).fnptr!();

        if (ExpandedMenuWaterActive)
            FhUtil.set_at(EngineAddresses.MenuWaterFrame, expanded_menu_water_next(before));
        else if (!advance_this_frame()) FhUtil.set_at(EngineAddresses.MenuWaterFrame, before);
        byte after = FhUtil.get_at<byte>(EngineAddresses.MenuWaterFrame);
        if (after != before) _menu_water_advances++;
        if (after < before) _menu_water_wraps++;
    }

    private long _menu_water_calls, _menu_water_advances, _menu_water_wraps;

    /* Upload cadence follows validated metadata; the authored movie-to-script conversion is unchanged. */
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
        if (abort_failed_av1_movie()) return;

        var original = new FhCallHandle<d_video_update>(new FhCallLocation(EngineAddresses.GraphicVideoUpdate, 0))
            .chain_from(h_video_update);

        if (!fmv_playing()) { original.fnptr!(); return; }

        _video_calls++;

        if (!advance_source_frame(ref _video_carry, _active_fmv_rate, TargetFramerate)) { _video_held++; return; }

        original.fnptr!();
    }

    /* The engine carries two texture animation formats side by side and picks one per character on
     * byte 2 of the descriptor. The new format's advance takes its step from Ch_TextureSetAnimTimer,
     * and is corrected within its own update call. This is the old format, whose counters are literal:
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

        new FhCallHandle<d_texanim_advance_old>(new FhCallLocation(EngineAddresses.ChrTexAnimAdvanceOld, 0))
            .chain_from(h_texanim_advance_old).fnptr?.Invoke(slot);
    }
}
