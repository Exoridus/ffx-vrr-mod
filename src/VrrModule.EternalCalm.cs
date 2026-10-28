namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The copyright card drawn over the Eternal Calm sequence.
///
///     Sg_MainLoop draws it inline, once per pass: it increments a frame counter, passes the new
///     value to ToDrawEternalCalmCopyRight, and then compares the counter against 0x130 to decide
///     whether the sequence is over. Both the counter and the card's alpha therefore advance once
///     per presented frame, so at 60 Hz the card fades in twice too fast and the sequence ends after
///     5 seconds instead of 10.
///
///     The card cannot simply be held, because a skipped call is a frame with no card on screen. It
///     is detoured instead: on a held frame the counter is put back where the caller found it and
///     the callee runs with the un-advanced clock, so the authored sequence is emitted at half rate
///     while every frame still draws.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private long _eternal_calm_calls;
    private long _eternal_calm_held;

    /// <summary>Card draws, of those the ones this frame was not allowed to advance.</summary>
    private string eternal_calm_counts()
        => $"calm={_eternal_calm_calls}/{_eternal_calm_held}";

    private bool init_eternal_calm_hook()
    {
        if (!_config.EternalCalmCard) return true;

        return hook_or_log(HookSiteId.EternalCalmToDrawEternalCalmCopyRight, "ToDrawEternalCalmCopyRight", EngineAddresses.ToDrawEternalCalmCopyRight,
            () => new FhCallHandle<d_eternal_calm_card>(new FhCallLocation(EngineAddresses.ToDrawEternalCalmCopyRight, 0))
                .hook(this, with_hook_continuation<d_eternal_calm_card>(h_eternal_calm_card)));
    }

    /* Cdecl with one argument, and the binary is what says so. The function ends in a plain c3 and
     * the call site cleans with add esp,4, so the caller cleans; the catalog's parameter count of
     * zero is an artefact of the call site pushing the argument out of a register before it stores
     * it, which leaves no data reference for the call-site counter to see. A StdCall delegate, or a
     * delegate with no parameter, drifts the stack of Sg_MainLoop once per frame. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_eternal_calm_card(int frames);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_eternal_calm_card(int frames)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_eternal_calm_card>(new FhCallLocation(EngineAddresses.ToDrawEternalCalmCopyRight, 0)).chain_from(h_eternal_calm_card).fnptr)(frames);
            return;
        }

        var original = new FhCallHandle<d_eternal_calm_card>(
            new FhCallLocation(EngineAddresses.ToDrawEternalCalmCopyRight, 0)).chain_from(h_eternal_calm_card);

        _eternal_calm_calls++;

        if (advance_this_frame())
        {
            original.fnptr!(frames);
            return;
        }

        _eternal_calm_held++;

        /* frames is what the caller just stored, so frames - 1 is the value it found: the write can
         * only ever move the counter down, and only by the one increment this pass made. The 0x130
         * compare sits after the call and reads the global back from memory, so the caller sees the
         * un-advanced value on this pass and does not end the sequence early. It still ends: every
         * advancing pass keeps its increment, and advance_this_frame is true at 1/Scale of frames,
         * so the counter rises without bound and crosses the threshold after 0x130 * Scale frames. */
        FhUtil.set_at(EngineAddresses.EternalCalmCardFrames, frames - 1);

        /* The alpha is the whole of the callee's persistent state, and it steps by the call rather
         * than by the argument: each call adds or subtracts 4 regardless of what it was passed.
         * Putting it back after the call is therefore the hold. The draw inside the call has already
         * used the stepped value, so this frame shows the next authored alpha, and the following
         * advancing call steps to that same value from the restored one - each authored step is on
         * screen for Scale frames instead of one. Restoring before the call would show a frozen
         * card; skipping the call would show none. */
        int alpha = FhUtil.get_at<int>(EngineAddresses.EternalCalmCardAlpha);
        original.fnptr!(frames - 1);
        FhUtil.set_at(EngineAddresses.EternalCalmCardAlpha, alpha);
    }
}
