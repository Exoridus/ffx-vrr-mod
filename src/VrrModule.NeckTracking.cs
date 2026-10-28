namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The head and neck tracking an actor runs while something has its attention: a weight that
///     ramps the override in and out, a yaw that blends towards whatever the actor is looking at,
///     and a fade that slerps out of the pose the previous target left behind.
///
///     All three are counted in calls and none of them reads sg_rate, so at 60 Hz all three run at
///     double speed. Two of them are not patchable even in principle: the yaw blend divides by 1.5
///     or multiplies by 0.25 out of shared .rdata constants that dozens of unrelated functions
///     read, and the slerp fade decrements by the same 0.25.
///
///     The call cannot be skipped. Its tail writes the replace matrix and the weight of optpos
///     element 9 - a pointer to the actor's own matrix at +0x46c, plus the weight - and the worker
///     runs Ch_CalcElement and Ch_OptposCalc over that element a few instructions later, so a frame
///     without the call is a frame drawn from whatever the element held before.
///
///     So the call runs every frame and its state is put back on the frames a 30 Hz game would not
///     have taken. Restoring after the fact is exact here, which it is not for the look-around next
///     door, because nothing in the body is random and nothing it applies survives into the next
///     call except through the fields that are restored:
///
///     <list type="bullet">
///     <item>The pose matrix at +0x46c is rebuilt from scratch every call - the normal path clears
///     it to identity and assigns the three basis vectors, the explicit-rotation path assigns it
///     from a unit matrix - so it never accumulates and does not need restoring. It must in fact
///     NOT be restored: Ch_NeckSetEnable snapshots it as the quaternion the next fade starts from,
///     and the pose that was actually drawn is the right thing for it to read.</item>
///     <item>The two element fields the tail writes are overwritten by the next call and read only
///     in between.</item>
///     <item>The three clocks and the side latch are the whole of the rest, and all four go back.</item>
///     </list>
///
///     The visible result is the engine's own sequence at half rate: on a held frame the call still
///     computes and applies the state one step ahead, and the following advancing frame recomputes
///     that same step from the restored state, so each authored value is drawn twice rather than
///     every other one being skipped. The recompute is not bit-identical, because the yaw blend
///     reads the target's position and the actor's facing afresh, but that is the input moving
///     under a 30 Hz clock rather than the clock itself running fast.
///
///     Ch_NeckSetSpeed needs no hook. It writes the divisor of the weight ramp, and scaling that
///     divisor would be the way to halve the ramp if the ramp were the only clock; holding the
///     state halves all three without touching a setter, and it leaves the two clocks whose rates
///     are shared constants correct as well.
///
///     Everything the hook needs lives in locals, so it is safe on the job threads Ch_CalcMain
///     dispatches this work to.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>Ch work record, float. The override weight, ramping towards +0x414 over +0x420.</summary>
    private const int NeckWeightOffset = 0x418;

    /// <summary>Ch work record, float. The applied yaw in degrees, blending towards the target.</summary>
    private const int NeckYawOffset = 0x41C;

    /// <summary>Ch work record, float. The fade out of the previous pose, 1.0 down to 0 by 0.25.</summary>
    private const int NeckFadeOffset = 0x4BC;

    /// <summary>
    ///     Ch work record, int. Which limit the yaw was pinned to when the target went behind the
    ///     actor. Not a clock, but the call writes it, and the two thresholds it selects between
    ///     differ, so letting a held frame flip it would move the hysteresis to the presented rate.
    /// </summary>
    private const int NeckSideOffset = 0x4D0;

    private long _neck_calls;
    private long _neck_held;

    /// <summary>Neck tracking calls, of those the ones whose clocks this frame put back.</summary>
    private string neck_counts()
        => $"neck={_neck_calls}/{_neck_held}";

    private bool init_neck_tracking_hook()
    {
        if (!_config.NeckTracking) return true;

        return hook_or_log(HookSiteId.NeckTrackingChNeckTracking, "Ch neck tracking", EngineAddresses.ChNeckCalc,
            () => new FhCallHandle<d_neck_calc>(new FhCallLocation(EngineAddresses.ChNeckCalc, 0))
                .hook(this, with_hook_continuation<d_neck_calc>(h_neck_calc)));
    }

    /* One argument, caller cleanup: both returns are a plain c3 and the only call site, in the
     * per-actor worker, pushes edi for this call and for the look-around before it and then clears
     * eight bytes once. The argument is the actor pointer - the entry loads [ebp+8] into esi and
     * passes it straight to Ch_OptposGetElement. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_neck_calc(nint actor);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_neck_calc(nint actor)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_neck_calc>(new FhCallLocation(EngineAddresses.ChNeckCalc, 0)).chain_from(h_neck_calc).fnptr)(actor);
            return;
        }

        var original = new FhCallHandle<d_neck_calc>(
            new FhCallLocation(EngineAddresses.ChNeckCalc, 0)).chain_from(h_neck_calc);

        /* Telemetry only, and incremented from the job threads, so the two counts are approximate
         * under contention. Nothing reads them but the sample line. */
        _neck_calls++;

        if (actor == 0 || advance_this_frame())
        {
            original.fnptr!(actor);
            return;
        }

        _neck_held++;

        float* weight = (float*)(actor + NeckWeightOffset);
        float* yaw    = (float*)(actor + NeckYawOffset);
        float* fade   = (float*)(actor + NeckFadeOffset);
        int*   side   = (int*)  (actor + NeckSideOffset);

        float saved_weight = *weight;
        float saved_yaw    = *yaw;
        float saved_fade   = *fade;
        int   saved_side   = *side;

        original.fnptr!(actor);

        /* The weight goes back even though its two clamps look idempotent. They are not: once the
         * ramp has crossed 0.99 the field is snapped to exactly 1.0, and once it has fallen to 0.01
         * it is snapped to 0 and the call returns early with the override switched off. Leaving
         * either snap in place on a held frame would end the ramp a frame sooner than 30 Hz. */
        *weight = saved_weight;
        *yaw    = saved_yaw;
        *fade   = saved_fade;
        *side   = saved_side;
    }
}
