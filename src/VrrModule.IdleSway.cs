namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The idle look-around an actor runs while it is standing still: a body-yaw offset that walks
///     towards a random target, dwells there for a few calls, and rolls a new target.
///
///     Every quantity in it is counted in calls. The dwell is rand() &amp; 3 + 1, so one to four calls;
///     the step is rand() % 0x14 + 1, so one to twenty tenths of a degree per call. Nothing in the
///     function reads sg_rate or KEEP_FPS, so at 60 Hz the whole look-around plays at double speed.
///
///     The call cannot be skipped. Its tail rebuilds the rotation of optpos element 0x0d from the
///     character resource - it assigns all three components rather than accumulating into them - and
///     Ch_OptposCalc consumes that element immediately afterwards in the same worker pass, so a
///     frame without the call is a frame whose element rotation nobody wrote.
///
///     So the call runs every frame and the advance inside it is suppressed on the frames a 30 Hz
///     game would not have taken. That is the copyright card's hold with one difference: there the
///     step is a constant, so letting the call advance and putting the state back afterwards shows
///     the next authored value one frame early and nothing else. Here the advance draws its next
///     target and step from rand(), so the value a held call would put on screen is not the value
///     the following advancing call produces - undoing the state would leave a visible one-frame
///     excursion of up to two degrees every time a re-roll landed on a held frame, which is roughly
///     one held frame in five. The advance is therefore prevented from happening at all, and the
///     apply block runs off the unchanged angle.
///
///     Two properties of the body make that possible without touching the code path itself. The
///     re-roll is gated on the dwell counter being zero, so any non-zero dwell skips it; and the
///     walk adds or subtracts the step and then only clamps when it has crossed the target, so a
///     step of zero writes the angle back unchanged and takes neither clamp. Both writes are undone
///     afterwards along with the two the call makes on its own.
///
///     Nothing outside this function reads the four fields, and everything the hook needs lives in
///     locals, so it is safe on the job threads Ch_CalcMain dispatches this work to.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>Ch work record, short. The yaw offset currently applied, in tenths of a degree.</summary>
    private const int IdleSwayAngleOffset = 0x4DC;

    /// <summary>Ch work record, short. The offset the walk is heading for, 0 to 69 tenths.</summary>
    private const int IdleSwayTargetOffset = 0x4DE;

    /// <summary>Ch work record, byte. Calls left before the target and the step are rolled again.</summary>
    private const int IdleSwayDwellOffset = 0x4E1;

    /// <summary>Ch work record, byte. How far the angle moves per call, in tenths of a degree.</summary>
    private const int IdleSwayStepOffset = 0x4E3;

    private long _idle_sway_calls;
    private long _idle_sway_held;

    /// <summary>Look-around calls, of those the ones this frame was not allowed to advance.</summary>
    private string idle_sway_counts()
        => $"idle_sway={_idle_sway_calls}/{_idle_sway_held}";

    private bool init_idle_sway_hook()
    {
        if (!_config.IdleSway) return true;

        return hook_or_log("Ch idle sway", EngineAddresses.ChIdleSway,
            () => new FhCallHandle<d_idle_sway>(new FhCallLocation(EngineAddresses.ChIdleSway, 0))
                .hook(this, h_idle_sway));
    }

    /* One argument, caller cleanup: the body ends 5f 5e 8b e5 5d c3 and its only call site pushes
     * edi and then clears eight bytes for this call and the aim blend together. The decompiler
     * types the argument float, which it is not - the entry loads [ebp+8] into edi and immediately
     * tests the byte at edi+0x4d4, so it is the actor pointer. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_idle_sway(nint actor);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_idle_sway(nint actor)
    {
        var original = new FhCallHandle<d_idle_sway>(
            new FhCallLocation(EngineAddresses.ChIdleSway, 0)).chain_from(h_idle_sway);

        /* Telemetry only, and incremented from the job threads, so the two counts are approximate
         * under contention. Nothing reads them but the sample line. */
        _idle_sway_calls++;

        if (actor == 0 || advance_this_frame())
        {
            original.fnptr!(actor);
            return;
        }

        _idle_sway_held++;

        short* angle  = (short*)(actor + IdleSwayAngleOffset);
        short* target = (short*)(actor + IdleSwayTargetOffset);
        byte*  dwell  = (byte*) (actor + IdleSwayDwellOffset);
        byte*  step   = (byte*) (actor + IdleSwayStepOffset);

        short saved_angle  = *angle;
        short saved_target = *target;
        byte  saved_dwell  = *dwell;
        byte  saved_step   = *step;

        *dwell = 1;
        *step  = 0;

        original.fnptr!(actor);

        /* All four go back, not just the two that were staged. The recentring branch the function
         * takes when the actor has stopped looking around ignores the dwell counter and rolls a
         * fresh step of its own, so that path does advance the angle once even here; restoring it
         * keeps the recentring itself at 30 Hz and leaves only the one frame it is drawn a step
         * early, for the handful of calls it takes to reach zero. */
        *angle  = saved_angle;
        *target = saved_target;
        *dwell  = saved_dwell;
        *step   = saved_step;
    }
}
