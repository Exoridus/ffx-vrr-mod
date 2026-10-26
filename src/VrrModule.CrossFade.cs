namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The camera cross-fade, which is slot 0 of the five-slot filter table Sg_DrawFilter steps once
///     per main loop pass.
///
///     The other four slots recompute their alpha every pass from a remaining count and the total
///     they were given - counter * 0x7f / total - so their duration in presented frames is exactly
///     the frame count their setter received, and doubling that count is already the whole
///     correction. Holding Sg_DrawFilter as a whole would make every fade and flash twice too slow,
///     which is why the hold below is scoped to two words of slot 0 rather than to the call.
///
///     Slot 0 is the exception. Sg_AccSetAlpha fixes a step of max(1, |alpha - current| / frames)
///     once, and the case walks the alpha by that step until it reaches the target; the counter only
///     self-holds at 1 while the alpha is non-zero. The step is an integer with a floor of 1, so
///     below |delta| < frames a doubled frame count cannot express a half step and the ramp keeps
///     the 30 Hz shape at 60 Hz speed. Holding the slot instead emits the engine's own sequence at
///     half rate, which divides nothing and is exact in both directions.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     Set whenever the Sg_AccSetAlpha detour runs, and read once by the hold below. Sg_DrawFilter
    ///     case '\x02' - a mode 2 Sg_Fade_Common on slot 4 - calls Sg_AccSetAlpha(0, 0) itself, which
    ///     clears slot 0 outright: counter and alpha both zero. Putting the pre-call values back over
    ///     that would resurrect a blur weight the engine has just switched off, and since nothing
    ///     lowers it again it would stay on screen for the rest of the scene.
    ///
    ///     Nothing else can move the flag inside the window. The window is one function long, and of
    ///     the five call sites of Sg_AccSetAlpha in the image only case '\x02' is reachable from
    ///     Sg_DrawFilter; the other four are the ATEL handler for call target 401A, the debug window
    ///     control, and two direct clears, none of which Sg_DrawFilter calls. Both functions run on
    ///     the main loop thread.
    /// </summary>
    private bool _acc_set_alpha_reentered;

    /// <summary>
    ///     Whether the Sg_AccSetAlpha detour is installed, and therefore whether the flag above can
    ///     ever be raised. Set by init_timing_hooks, which runs first.
    /// </summary>
    private bool _acc_set_alpha_hooked;

    private long _cross_fade_steps;
    private long _cross_fade_held_frames;
    private long _cross_fade_restores;
    private long _cross_fade_reentrant;

    /// <summary>
    ///     Stepping passes, of those the ones this frame was not allowed to advance, of those the
    ///     ones actually put back, and how often the re-entrancy guard took the restore away. The
    ///     third and fourth always sum to the second; a non-zero fourth is the guard doing its job.
    /// </summary>
    private string cross_fade_counts()
        => $"xfade={_cross_fade_steps}/{_cross_fade_held_frames}/{_cross_fade_restores}/{_cross_fade_reentrant}";

    /// <summary>
    ///     Whether the hold is actually in place, which is what the Sg_AccSetAlpha detour scales on
    ///     rather than the configuration flag. The two corrections are one switch, and a refused hook
    ///     is the one way they could come apart: the setter is installed first, so without this it
    ///     would already have stopped scaling by the time the hold failed and slot 0 would run at the
    ///     presented rate with nothing correcting it.
    /// </summary>
    private bool _cross_fade_hold_active;

    private bool init_cross_fade_hook()
    {
        if (!_config.CrossFadeHold) return true;

        // Without the setter detour the re-entrancy guard cannot fire, and the hold would put back
        // an alpha the engine had just cleared and leave the blur on screen. The uncorrected ramp
        // that skipping the hold leaves behind is the lesser of the two.
        if (!_acc_set_alpha_hooked)
        {
            _logger.Error("[Vrr] Sg_AccSetAlpha is not hooked, so the cross-fade hold has no " +
                          "re-entrancy guard and was not installed.");
            return false;
        }

        // The operands the upstream FFX-60fps UI-001 row locates by scan, checked in place instead.
        nint fn = EngineAddresses.SgDrawFilter;
        _filter_presentation_hold = fx_operand_is(fn + 0x12, EngineAddresses.FilterActiveMask)
            && fx_code_matches(fn + 0x28a, "A3 ?? ?? ?? ?? C7 05 ?? ?? ?? ?? 01 00 00 00 D9 1D")
            && fx_operand_is(fn + 0x28b, EngineAddresses.FilterFadeType)
            && fx_operand_is(fn + 0x291, EngineAddresses.FilterOutputs);
        if (!_filter_presentation_hold)
            _logger.Error("[Vrr] Sg_DrawFilter operands differ from build 25501027; screen fades keep the native clock.");

        _cross_fade_hold_active = hook_or_log(HookSiteId.CrossFadeSgDrawFilter, "Sg_DrawFilter", EngineAddresses.SgDrawFilter,
            () => new FhCallHandle<d_draw_filter>(new FhCallLocation(EngineAddresses.SgDrawFilter, 0))
                .hook(this, h_draw_filter));
        _filter_presentation_hold &= _cross_fade_hold_active;

        return _cross_fade_hold_active;
    }

    /// <summary>
    ///     Whether presentation passes draw the filter table without stepping it. Without it the
    ///     render gate falls back to the native clock while a filter slot is active.
    /// </summary>
    private bool _filter_presentation_hold;
    private long _filter_presentation_holds, _filter_presentation_changed;

    // Mask, five slots and two dwords the function reads.
    private const int FilterStateBytes = 0x58;

    /// <summary>Presentation passes drawn with the filter state put back, and of those the ones the call had changed.</summary>
    private string filter_hold_counts() => $"fhold={_filter_presentation_holds}/{_filter_presentation_changed}";

    /* A presentation pass draws the filters, then gets the state back, so only simulation passes
     * count fades down (upstream UI-001). A snapshot taken anywhere but around the call itself has
     * to prove that no step falls outside its window; around the call nothing can. Restoring the
     * state also covers the Sg_AccSetAlpha(0, 0) clear in case 2: the next simulation pass runs the
     * same case and clears again. The outputs are not put back, unlike upstream: they are this
     * image's draw request, recomputed from the state on every call, and restoring them dropped the
     * fade from every presented image that was not a simulation pass (dark/bright flicker). */
    private void draw_filter_presentation(FhCallHandle<d_draw_filter> original)
    {
        byte* state = stackalloc byte[FilterStateBytes];
        byte* live = FhUtil.ptr_at<byte>(EngineAddresses.FilterActiveMask);
        Buffer.MemoryCopy(live, state, FilterStateBytes, FilterStateBytes);

        original.fnptr!();

        if (!new ReadOnlySpan<byte>(live, FilterStateBytes).SequenceEqual(new ReadOnlySpan<byte>(state, FilterStateBytes)))
            _filter_presentation_changed++;
        Buffer.MemoryCopy(state, live, FilterStateBytes, FilterStateBytes);
        _filter_presentation_holds++;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_draw_filter();

    /* Snapshot and restore rather than a skip, because the other four slots in the same call must
     * advance. The two words below are everything the slot 0 case writes to the slot: the counter
     * and the current alpha. It leaves the target and the step alone, and it can only clear the
     * table's active mask on the pass where the counter is already zero - which is the pass where
     * the case does not run at all, so the guard on counter != 0 keeps the mask out of the window.
     *
     * The dream fade globals the case also touches are deliberately not restored. They are derived
     * from the counter every pass, so a held pass writes exactly what the following advancing pass
     * writes and they never drift; and slot 4 writes the same globals later in the same loop, so
     * putting slot 0's values back would overwrite a fade that must not be held. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_draw_filter()
    {
        var original = new FhCallHandle<d_draw_filter>(new FhCallLocation(EngineAddresses.SgDrawFilter, 0))
            .chain_from(h_draw_filter);

        if (_filter_presentation_hold && RenderPassContext.IsPresentation)
        {
            draw_filter_presentation(original);
            return;
        }

        short counter = FhUtil.get_at<short>(EngineAddresses.CrossFadeSlot0Counter);
        short alpha   = FhUtil.get_at<short>(EngineAddresses.CrossFadeSlot0Alpha);

        bool hold = counter != 0 && !advance_this_frame();
        if (counter != 0) _cross_fade_steps++;
        if (hold) _cross_fade_held_frames++;

        _acc_set_alpha_reentered = false;
        original.fnptr!();

        if (!hold) return;

        if (_acc_set_alpha_reentered) { _cross_fade_reentrant++; return; }

        FhUtil.set_at(EngineAddresses.CrossFadeSlot0Counter, counter);
        FhUtil.set_at(EngineAddresses.CrossFadeSlot0Alpha, alpha);
        _cross_fade_restores++;
    }
}
