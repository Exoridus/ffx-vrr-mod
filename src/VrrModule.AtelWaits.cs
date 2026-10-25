namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_wait_init(nint work, int* storage, nint stack);

    /* The dispatcher calls every exec handler as (worker, storage) and reads a four byte result, so
     * the return is an int rather than a bool: it is passed straight back and never inspected here. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_atel_wait_exec(nint work, int* storage);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_movie_frame_delta();

    /* ATEL call target 0000, the frame-based wait. A wait of one frame is an idle loop rather than a
     * duration, so scaling it would change control flow instead of timing. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_atel_wait_init(nint work, int* storage, nint stack)
    {
        new FhCallHandle<d_atel_wait_init>(new FhCallLocation(EngineAddresses.AtelWaitInit, 0))
            .chain_from(h_atel_wait_init).fnptr!(work, storage, stack);

        // The native initializer applies scene-specific waits before duration scaling.
        if (_atel_wait_hooks_ready && *storage != 1)
            *storage = (int)Math.Clamp((double)*storage * Scale, int.MinValue, int.MaxValue);
    }

    /// <summary>
    ///     The movie branch's whole correction state, thread local as one piece. The flag is raised
    ///     only for the length of the chained exec handler and read only by the movie frame delta
    ///     detour that handler may call, one synchronous call away on the same stack; the carry holds
    ///     the remainder of the value that detour hands back to the engine.
    ///
    ///     They are isolated together because isolating only the flag would be incoherent: it asserts
    ///     that two ATEL workers may be stepped from two threads, and then lets those two threads
    ///     interleave the one read-modify-write that decides how long a wait lasts.
    /// </summary>
    private bool _atel_wait_hooks_ready;
    [ThreadStatic] private static bool _inside_atel_wait_exec;
    [ThreadStatic] private static double _movie_wait_carry;

    /* Telemetry only, and deliberately not isolated the way the two fields above are. A counter that
     * loses an increment to a race costs a line in a log, while making them thread local would report
     * each thread's share instead of the total and hide the very case the isolation is there for.
     * Nothing the engine sees is derived from them. */
    private long _movie_wait_calls;
    private long _movie_wait_delta;
    private long _movie_wait_scaled;

    /// <summary>
    ///     Movie branch entries of the ATEL wait, and the raw and corrected movie frame deltas they
    ///     subtracted. All zero outside the four movies movie_have_camera admits.
    /// </summary>
    private string atel_wait_counts()
        => $"atelmv={_movie_wait_calls}/{_movie_wait_delta}/{_movie_wait_scaled}";

    /* Nothing but the flag. The exec handler's normal branch is the hot one - 53,676 call sites reach
     * this call target - and it is already correct, so re-implementing the body in order to reach the
     * movie branch would put the path that works at risk to fix the one that does not. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_atel_wait_exec(nint work, int* storage)
    {
        bool previous = _inside_atel_wait_exec;
        _inside_atel_wait_exec = _atel_wait_hooks_ready;
        try
        {
            return new FhCallHandle<d_atel_wait_exec>(new FhCallLocation(EngineAddresses.AtelWaitExec, 0))
                .chain_from(h_atel_wait_exec).fnptr!(work, storage);
        }
        finally
        {
            _inside_atel_wait_exec = previous;
        }
    }

    /* The movie branch of the wait subtracts elapsed movie frames instead of one, and the counter it
     * subtracts them from was multiplied by Scale at init, so a wait of N frames outlasts N movie
     * frames. Correcting the counter is not an option: it is the same counter the normal branch walks
     * down one at a time, and the branch is chosen per call rather than per wait.
     *
     * The delta is 0 or 1 per call, so it is multiplied and never divided - dividing it would floor to
     * zero every call and the wait would never end. The remainder is carried the way the vertical blank
     * delta is, so a non-integer Scale averages out instead of truncating a fifth of the movie away.
     *
     * The flag keeps this off the other caller, the ATEL camera interpolator, which needs the same
     * correction expressed against a float accumulator rather than this counter. movie_have_camera,
     * the only other thing the wait calls inside the window, reaches nothing that lands back here.
     *
     * A delta below zero is passed through unscaled rather than multiplied like a positive one. It
     * means the manager's frame number moved backwards, which is a movie being restarted rather than
     * time passing, and the engine reads it as the wait counter growing back. Scaling it would make
     * the counter grow twice as fast on a transient that is not a duration at all. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_movie_frame_delta()
    {
        int delta = new FhCallHandle<d_movie_frame_delta>(new FhCallLocation(EngineAddresses.MovieFrameDelta, 0))
            .chain_from(h_movie_frame_delta).fnptr!();

        if (!_inside_atel_wait_exec) return delta;

        _movie_wait_calls++;
        if (delta <= 0) return delta;

        _movie_wait_delta += delta;
        _movie_wait_carry += delta * Scale;

        int scaled = (int)_movie_wait_carry;
        _movie_wait_carry -= scaled;
        _movie_wait_scaled += scaled;
        return scaled;
    }

}
