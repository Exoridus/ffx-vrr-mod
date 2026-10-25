namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The two frame-bound counters inside the motion path that sg_rate does not reach.
///
///     The rate correction in the motion advance multiplies one thing: the accumulator step. Neither
///     the cross-fade length nor the sequence VM's wait ever sees it, so both count raw calls and
///     both run twice as fast at 60 Hz whether or not the actor is engine-corrected. That is why
///     these two are held for every actor rather than gated on the 0x100000 flag the speed scaling
///     is gated on - the flag decides whether the engine corrects the step, and the step is not what
///     is wrong here.
///
///     Both corrections are the same shape: let the original run, then undo its decrement on the
///     frames a 30 Hz sequence would not have advanced.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>Ch work record, ushort index 0x3a9. The remaining call count of a cross-fade.</summary>
    private const int HokanOffset = 0x752;

    /// <summary>Ch work record. The sequence VM's wait counter, opcode 3. Zero outside a wait.</summary>
    private const int SeqWaitOffset = 0x72a;

    private long _hokan_calls;
    private long _hokan_held;
    private long _seq_calls;
    private long _seq_held;

    private string motion_sequence_counts()
        => $"hokan={_hokan_calls}/{_hokan_held} seqwait={_seq_calls}/{_seq_held}";

    private bool init_motion_sequence_hooks()
    {
        if (!_config.MotionSequenceCounters) return true;

        bool ok = hook_or_log("Ch_SeqFrame", EngineAddresses.ChSeqFrame,
            () => new FhCallHandle<d_seq_frame>(new FhCallLocation(EngineAddresses.ChSeqFrame, 0))
                .hook(this, h_seq_frame));

        ok &= hook_or_log("motion cross-fade", EngineAddresses.ChMotionInterpolate,
            () => new FhCallHandle<d_motion_interpolate>(new FhCallLocation(EngineAddresses.ChMotionInterpolate, 0))
                .hook(this, h_motion_interpolate));

        return ok;
    }

    /* Both are caller-cleanup by measurement (a plain ret), so cdecl. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_seq_frame(nint work);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_motion_interpolate(nint work, int hokan);

    /* The sequence VM. Opcode 3 loads a 16-bit immediate into the wait counter the first time it
     * runs and decrements it once per call; the counter reaches zero exactly when the wait is over,
     * and is zero at every other moment.
     *
     * Adding one back on a held frame doubles the wait. The counter must already be running: a value
     * of zero is not a wait in progress but the state the opcode uses to decide it needs to load its
     * immediate, and writing 1 there would make the next wait end after a single call. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_seq_frame(nint work)
    {
        _seq_calls++;

        new FhCallHandle<d_seq_frame>(new FhCallLocation(EngineAddresses.ChSeqFrame, 0))
            .chain_from(h_seq_frame).fnptr?.Invoke(work);

        if (_config.MotionSequenceHold && !advance_this_frame() && work != 0)
        {
            short* wait = (short*)(work + SeqWaitOffset);
            if (*wait > 0) { *wait += 1; _seq_held++; }
        }
    }

    /* The cross-fade blends every channel by (target - current) / hokan and the advance then counts
     * hokan down, so the fade lands exactly on target after hokan calls. Twice the calls per second
     * is twice the speed, and the argument cannot be scaled instead: a larger divisor with an
     * unchanged countdown would end the fade short of its target and snap.
     *
     * The advance floors the counter at 1 and treats 1 as the final call, so a held frame is only
     * compensated while there is more than one call left. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_motion_interpolate(nint work, int hokan)
    {
        _hokan_calls++;

        new FhCallHandle<d_motion_interpolate>(new FhCallLocation(EngineAddresses.ChMotionInterpolate, 0))
            .chain_from(h_motion_interpolate).fnptr?.Invoke(work, hokan);

        if (_config.MotionSequenceHold && !advance_this_frame() && work != 0)
        {
            short* remaining = (short*)(work + HokanOffset);
            if (*remaining > 1) { *remaining += 1; _hokan_held++; }
        }
    }
}
