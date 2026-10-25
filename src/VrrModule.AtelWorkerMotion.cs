namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The ATEL worker's own motion and rotation, which is how every event character walks and
///     turns, how the field and event camera travels, and how an attached map group or map part
///     moves.
///
///     Each worker carries nine move records at +0x5b8, stride 0x4c, and nine rotation records at
///     +0x864, stride 0x44 - one per thread priority, reached through the thread's own +0x44 and
///     +0x48. Two readers step the current pair once per ATEL pass, which is once per presented
///     frame, and every quantity they advance is counted in calls rather than in time:
///
///     <list type="bullet">
///     <item>a yaw and a pitch turn step in radians per call, and four rotation rates;</item>
///     <item>two watchdog deadlines, compared against a counter each reader increments itself, past
///     which the step is multiplied by counter over deadline up to eight times;</item>
///     <item>a float interpolation accumulator advanced by one per call and divided by a duration,
///     which is how the spline, linear and parabolic move types and the timed rotation types
///     express their progress;</item>
///     <item>the actor's speed, integrated into its position once per call for every worker that is
///     not a Ch character.</item>
///     </list>
///
///     Nothing in the engine scales any of it. At 60 Hz an event character turns at twice the
///     authored angular velocity, a scripted rotation finishes in half its authored time while the
///     wait placed after it now runs twice as long, and the field camera dollies twice as fast.
///
///     Four corrections, and each one starts from the same test: a scale of one means the engine is
///     already pacing itself from the syncdata table, and then nothing here changes anything.
///
///     <para>The rates are divided at their setters.</para> Each is a leaf function with one caller,
///     its ATEL opcode handler, and the field it writes is the only place the value lives. Dividing
///     at the reader is not available: the rates are read at four statements across the two readers
///     and combined there with the watchdog multiplier and the rotation ramp's own ease weight, so
///     there is no single point to correct.
///
///     <para>The deadlines are multiplied at theirs.</para> Scaling only the step would leave the
///     watchdog firing halfway through every long turn and accelerating its second half; scaling
///     only the deadline moves a line that nothing crosses. The pair is what reproduces 30 Hz.
///
///     <para>The accumulator is corrected at the increment, not at the duration.</para> The
///     duration setters look like the obvious lever and are the trap in this cluster: startMotion
///     and startRotation overwrite the duration field with its reciprocal for half the types, so a
///     multiplication there silently becomes a division of a field nothing then reads, and the
///     linear move type takes its step from the FMV decoder's own frame index while a movie with
///     its own camera is playing, which is wall clock and already right. So the readers are wrapped
///     instead, and the accumulator is rewritten by the delta the reader was just seen to produce.
///
///     <para>The translation is corrected at the integrator.</para> Not at the speed setter: the
///     same field reaches Ch_SetSp for a loadModel worker, whose motion Ch_CalcMain already
///     integrates against a corrected delta, and halving it there would halve every event
///     character's walking speed on top of a correction that works.
///
///     Every hook keeps its state in locals. The readers run on the ATEL pass rather than on a
///     Ch_CalcMain job thread, but nothing here would care either way.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>Work thread, pointer. The move record the current priority level is bound to.</summary>
    private const int AtelThreadMoveRecord = 0x44;

    /// <summary>Work thread, pointer. The rotation record for the same level.</summary>
    private const int AtelThreadRotRecord = 0x48;

    /// <summary>Either record, ushort. The type in the low bits, flags above it.</summary>
    private const int AtelRecordType = 0x02;

    /// <summary>Either record, float. Progress through the interpolation, in calls.</summary>
    private const int AtelRecordProgress = 0x08;

    /// <summary>
    ///     Worker, byte. How the worker is bound: 0 detached, 1 loadModel, 2 attachToCamera,
    ///     3 unused, 4 attachToMapGroup, 5 attachToMapPart. Only 2 through 5 reach the integrator.
    /// </summary>
    private const int AtelWorkerBindKind = 0xAA;

    /// <summary>Worker, float array. The actor transform of a script type 5 or 6 worker.</summary>
    private const int AtelActorTransformSmall = 0x284;

    /// <summary>Worker, float array. The actor transform of every other script type.</summary>
    private const int AtelActorTransform = 0x558;

    /// <summary>
    ///     Actor transform, float. The speed the move reader wrote this call, which the integrator
    ///     adds along the two Euler angles at +0x20 and +0x24.
    /// </summary>
    private const int AtelActorSpeed = 0x0C;

    /// <summary>
    ///     The twelve rate setters, by the two shapes their ATEL opcodes come in: one that writes
    ///     all nine priority levels and one that takes the level as its second argument.
    /// </summary>
    private static readonly (string Name, nint Rva)[] AtelRateSettersAllLevels =
    [
        ("setYawTurnStepAllLevels [006Dh]",   EngineAddresses.AtelSetYawTurnStepAllLevels),
        ("setPitchTurnStepAllLevels [006Eh]", EngineAddresses.AtelSetPitchTurnStepAllLevels),
        ("setAllRotationRate1 [006Fh]",       EngineAddresses.AtelSetYawRotRateAllLevels),
        ("setAltRotationRate1 [00E4h]",       EngineAddresses.AtelSetAltYawRotRateAllLevels),
        ("setAllRotationRate2 [0070h]",       EngineAddresses.AtelSetPitchRotRateAllLevels),
        ("setAllRotationRate3 [0071h]",       EngineAddresses.AtelSetRollRotRateAllLevels),
    ];

    private static readonly (string Name, nint Rva)[] AtelRateSettersOneLevel =
    [
        ("setYawTurnStep [002Bh]",   EngineAddresses.AtelSetYawTurnStep),
        ("setPitchTurnStep [002Ch]", EngineAddresses.AtelSetPitchTurnStep),
        ("setRotationSpeed1 [002Eh]", EngineAddresses.AtelSetYawRotRate),
        ("setAltRotationSpeed1 [00E3h]", EngineAddresses.AtelSetAltYawRotRate),
        ("setRotationSpeed2 [002Fh]", EngineAddresses.AtelSetPitchRotRate),
        ("setRotationSpeed3 [0030h]", EngineAddresses.AtelSetRollRotRate),
    ];

    /// <summary>
    ///     The four watchdog deadline setters, in the same two shapes. Three of the four have no
    ///     call site anywhere in the shipped script corpus, so in practice only the rotation
    ///     record's 90 frame default and setTurningDuration's 736 sites are ever in play; they are
    ///     hooked anyway because leaving one out would make a script that does use it the one case
    ///     nobody had thought about.
    /// </summary>
    private static readonly (string Name, nint Rva)[] AtelDeadlineSettersAllLevels =
    [
        ("setTurningDuration [0074h]", EngineAddresses.AtelSetTurnDeadlineAllLevels),
        ("ATEL [0075h]", EngineAddresses.AtelSetRotDeadlineAllLevels),
    ];

    private static readonly (string Name, nint Rva)[] AtelDeadlineSettersOneLevel =
    [
        ("ATEL [0072h]", EngineAddresses.AtelSetTurnDeadline),
        ("ATEL [0073h]", EngineAddresses.AtelSetRotDeadline),
    ];

    /* The chain is keyed by delegate instance, so each of the sixteen setters needs one of its own;
     * the list is also what keeps them alive against the collector. */
    private readonly List<Delegate> _atel_setter_hooks = [];

    private long _atel_rate_sets;
    private long _atel_deadline_sets;
    private long _atel_reads;
    private long _atel_progress_rewrites;
    private long _atel_movie_progress;
    private long _atel_integrator_calls;
    private long _atel_integrator_scaled;
    private long _atel_gravity_sets;

    /// <summary>
    ///     Why the integrator declined a worker, and with which bind kind and script type.
    ///
    ///     The scaled counter read zero through an entire blitzball match while the buoyancy hold was
    ///     holding every second pass, so the stadium's objects fall in one of the integrator's three
    ///     pass-through cases and nothing said which. Without that the fix is a guess: bind kind and
    ///     script type are the two fields the decision is made on, so they are what gets counted.
    /// </summary>
    private readonly Dictionary<(int Kind, int Type), long> _atel_declined = [];
    private long _atel_declined_header_null;

    /// <summary>
    ///     Rate setter calls, deadline setter calls, reader calls, accumulator rewrites and the
    ///     reader calls whose accumulator was left to the movie clock; then integrator calls and
    ///     the ones whose speed was scaled. A group that installs but never fires reads as zero
    ///     here, which is otherwise indistinguishable from a group that was never installed.
    /// </summary>
    private string atel_worker_motion_counts()
        => $"atelwrk={_atel_rate_sets}/{_atel_deadline_sets}/{_atel_reads}/{_atel_progress_rewrites}/" +
           $"{_atel_movie_progress} atelpos={_atel_integrator_calls}/{_atel_integrator_scaled}" +
           $" atelgrav={_atel_gravity_sets}" +
           atel_declined_counts();

    /// <summary>
    ///     The pass-through census, as kind:type=count pairs ordered so two runs print alike. Empty
    ///     while every worker the integrator sees is one it corrects.
    /// </summary>
    private string atel_declined_counts()
    {
        if (_atel_declined.Count == 0 && _atel_declined_header_null == 0) return string.Empty;

        var pairs = _atel_declined
            .OrderBy(e => e.Key.Kind).ThenBy(e => e.Key.Type)
            .Select(e => $"k{e.Key.Kind}t{e.Key.Type}={e.Value}");

        return $" atelskip=[{string.Join(' ', pairs)}" +
               (_atel_declined_header_null != 0 ? $" hdr0={_atel_declined_header_null}" : "") + "]";
    }

    /// <summary>
    ///     A watchdog deadline at the corrected rate. It is stored sixteen bits wide and both
    ///     readers compare it as a short, so an unclamped product that wrapped would turn a dormant
    ///     watchdog into one that fires on the first call.
    ///
    ///     Rounded rather than truncated, and without a carry. A carry belongs to a quantity that
    ///     is consumed repeatedly out of one accumulator; this is a field written outright by an
    ///     opcode that may next be aimed at a different worker, so carrying a remainder between two
    ///     calls would make two identical script statements produce two different deadlines. The
    ///     rounding error it would buy back is at most half a frame on a threshold whose effect
    ///     ramps continuously and is capped at eight times.
    /// </summary>
    private static int scale_deadline(int frames, float scale)
        => Math.Clamp((int)MathF.Round(frames * scale), short.MinValue, short.MaxValue);

    private bool init_atel_worker_motion_hooks()
    {
        bool ok = true;

        if (_config.AtelWorkerMotion)
        {
            ok &= hook_or_log("ATEL motion record defaults", EngineAddresses.AtelInitMotionRecords,
                () => new FhCallHandle<d_atel_init_records>(new FhCallLocation(EngineAddresses.AtelInitMotionRecords, 0))
                    .hook(this, h_atel_init_records));
            foreach ((string name, nint rva) in AtelRateSettersAllLevels)
            {
                d_atel_rate_all? self = null;
                self = (worker, rate) =>
                {
                    _atel_rate_sets++;
                    float scale = Scale;

                    new FhCallHandle<d_atel_rate_all>(new FhCallLocation(rva, 0))
                        .chain_from(self!).fnptr!(worker, scale == 1f ? rate : rate / scale);
                };

                _atel_setter_hooks.Add(self);

                d_atel_rate_all hook = self;
                ok &= hook_or_log(name, rva,
                    () => new FhCallHandle<d_atel_rate_all>(new FhCallLocation(rva, 0)).hook(this, hook));
            }

            foreach ((string name, nint rva) in AtelRateSettersOneLevel)
            {
                d_atel_rate_level? self = null;
                self = (worker, level, rate) =>
                {
                    _atel_rate_sets++;
                    float scale = Scale;

                    new FhCallHandle<d_atel_rate_level>(new FhCallLocation(rva, 0))
                        .chain_from(self!).fnptr!(worker, level, scale == 1f ? rate : rate / scale);
                };

                _atel_setter_hooks.Add(self);

                d_atel_rate_level hook = self;
                ok &= hook_or_log(name, rva,
                    () => new FhCallHandle<d_atel_rate_level>(new FhCallLocation(rva, 0)).hook(this, hook));
            }

            foreach ((string name, nint rva) in AtelDeadlineSettersAllLevels)
            {
                d_atel_deadline_all? self = null;
                self = (worker, frames) =>
                {
                    _atel_deadline_sets++;
                    float scale = Scale;

                    new FhCallHandle<d_atel_deadline_all>(new FhCallLocation(rva, 0))
                        .chain_from(self!).fnptr!(worker, scale == 1f ? frames : scale_deadline(frames, scale));
                };

                _atel_setter_hooks.Add(self);

                d_atel_deadline_all hook = self;
                ok &= hook_or_log(name, rva,
                    () => new FhCallHandle<d_atel_deadline_all>(new FhCallLocation(rva, 0)).hook(this, hook));
            }

            foreach ((string name, nint rva) in AtelDeadlineSettersOneLevel)
            {
                d_atel_deadline_level? self = null;
                self = (worker, level, frames) =>
                {
                    _atel_deadline_sets++;
                    float scale = Scale;

                    new FhCallHandle<d_atel_deadline_level>(new FhCallLocation(rva, 0))
                        .chain_from(self!).fnptr!(worker, level, scale == 1f ? frames : scale_deadline(frames, scale));
                };

                _atel_setter_hooks.Add(self);

                d_atel_deadline_level hook = self;
                ok &= hook_or_log(name, rva,
                    () => new FhCallHandle<d_atel_deadline_level>(new FhCallLocation(rva, 0)).hook(this, hook));
            }

            ok &= hook_or_log("ATEL move record reader", EngineAddresses.AtelMoveRecordRead,
                () => new FhCallHandle<d_atel_worker_pass>(new FhCallLocation(EngineAddresses.AtelMoveRecordRead, 0))
                    .hook(this, h_atel_move_read));

            ok &= hook_or_log("ATEL rotation record reader", EngineAddresses.AtelRotRecordRead,
                () => new FhCallHandle<d_atel_worker_pass>(new FhCallLocation(EngineAddresses.AtelRotRecordRead, 0))
                    .hook(this, h_atel_rot_read));
        }

        // Same shape as the all-levels rate setters - two arguments, the level read out of
        // worker+0x32 - and the same correction, because the field is added once per call.
        if (_config.AtelWorkerGravity)
        {
            nint grav_rva = EngineAddresses.AtelSetMoveGravity;
            d_atel_rate_all? grav_self = null;
            grav_self = (worker, g) =>
            {
                _atel_gravity_sets++;
                float scale = Scale;

                new FhCallHandle<d_atel_rate_all>(new FhCallLocation(grav_rva, 0))
                    .chain_from(grav_self!).fnptr!(worker, scale == 1f ? g : g / scale);
            };

            _atel_setter_hooks.Add(grav_self);

            d_atel_rate_all grav_hook = grav_self;
            ok &= hook_or_log("setGravity [0094h]", grav_rva,
                () => new FhCallHandle<d_atel_rate_all>(new FhCallLocation(grav_rva, 0)).hook(this, grav_hook));
        }

        if (_config.AtelWorkerTranslation)
        {
            ok &= hook_or_log("ATEL worker position integrator", EngineAddresses.AtelWorkerIntegratePos,
                () => new FhCallHandle<d_atel_worker_pass>(new FhCallLocation(EngineAddresses.AtelWorkerIntegratePos, 0))
                    .hook(this, h_atel_integrate_position));
        }

        return ok;
    }

    // --- Delegates ---

    /* All four setter shapes are cdecl, measured: every one of the sixteen ends in 5d c3 rather
     * than a c2 imm16, so the caller cleans. The catalog reports __stdcall for all of them, which
     * is its default rather than a measurement.
     *
     * The rate is declared float because the setters load it with fld dword [ebp+0xc] and store it
     * straight through. The deadline is declared int, not ushort: the call sites push a full dword
     * and the setter stores sixteen bits of it with 66 89. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_rate_all(nint worker, float rate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_rate_level(nint worker, int level, float rate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_deadline_all(nint worker, int frames);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_deadline_level(nint worker, int level, int frames);

    /* The two readers and the integrator share a signature and a call site. Ghidra types the
     * rotation reader's second parameter float and declares the integrator with one parameter; both
     * are wrong, and a binding built from either hands the function garbage. All three take the
     * worker and the work thread, and their caller pushes two arguments for each and then clears
     * the whole group at once. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_atel_worker_pass(nint worker, nint thread);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_movie_have_camera();

    // --- Handlers ---

    /// <summary>
    ///     Puts back the one call of progress the reader just added, as the fraction of a call the
    ///     corrected rate is worth.
    ///
    ///     The equality against exactly one is the whole guard, and it is what keeps the correction
    ///     off every path that is not the plain increment. The spline move types abort by writing
    ///     the duration plus one into the accumulator in a single step, which is a sentinel rather
    ///     than a tick; the parabolic type does not advance at all during its wind-up delay; and a
    ///     type whose record was re-armed inside the call did not tick either. None of those has a
    ///     delta of one, so all of them pass through untouched.
    /// </summary>
    private void rewrite_progress(nint rec, float before, float scale)
    {
        float* progress = (float*)(rec + AtelRecordProgress);
        if (*progress - before != 1f) return;

        *progress = before + 1f / scale;
        _atel_progress_rewrites++;
    }

    /* Move types 5, 6, 7 and 9 advance the accumulator; 1 to 4 use the turn steps and the speed
     * instead and are corrected elsewhere.
     *
     * Type 7 is the one that carries the ATEL wait's movie branch verbatim - the same gMoviePlay
     * plus movie_have_camera gate and the same delta out of the FMV decoder's own frame index - and
     * it is the reason the gate is evaluated here rather than inferred from the delta. That delta
     * is zero or one per call against a 29.97 fps container, so a movie-driven step of exactly one
     * is the common case and a test on the delta alone would divide the wall clock.
     *
     * This is also the whole of the interaction with the wait's own correction, and there is none.
     * That correction scales the decoder's delta only while a thread-local flag raised by the wait's
     * exec handler is set, and the wait handler's entire body is the gate, the delta and a
     * subtraction - it cannot reach the worker pass, which the VM step runs only after the opcode
     * loop has yielded. So this reader sees the raw delta, which is what leaving it alone requires. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_atel_move_read(nint worker, nint thread)
    {
        var original = new FhCallHandle<d_atel_worker_pass>(
            new FhCallLocation(EngineAddresses.AtelMoveRecordRead, 0)).chain_from(h_atel_move_read);

        _atel_reads++;

        float scale = Scale;
        nint  rec   = thread == 0 ? 0 : *(nint*)(thread + AtelThreadMoveRecord);

        if (scale == 1f || rec == 0)
        {
            original.fnptr!(worker, thread);
            return;
        }

        int  type         = *(ushort*)(rec + AtelRecordType) & 0x7f;
        bool interpolated = type is 5 or 6 or 7 or 9;

        if (type == 7
            && FhUtil.get_at<uint>(EngineAddresses.GMoviePlay) != 0
            && new FhCallHandle<d_movie_have_camera>(
                   new FhCallLocation(EngineAddresses.MovieHaveCamera, 0)).fnptr!() != 0)
        {
            interpolated = false;
            _atel_movie_progress++;
        }

        float before = interpolated ? *(float*)(rec + AtelRecordProgress) : 0f;

        original.fnptr!(worker, thread);

        if (interpolated) rewrite_progress(rec, before, scale);
    }

    /* Rotation types 3 and 4 advance the accumulator by one. Type 2 advances it by the record's own
     * per-call fraction instead, and startRotation derives that fraction from the yaw rate the rate
     * setters have already divided, so type 2 is correct without being touched and is excluded by
     * the type test rather than by a guard. There is no movie branch in this reader. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_atel_rot_read(nint worker, nint thread)
    {
        var original = new FhCallHandle<d_atel_worker_pass>(
            new FhCallLocation(EngineAddresses.AtelRotRecordRead, 0)).chain_from(h_atel_rot_read);

        _atel_reads++;

        float scale = Scale;
        nint  rec   = thread == 0 ? 0 : *(nint*)(thread + AtelThreadRotRecord);

        if (scale == 1f || rec == 0)
        {
            original.fnptr!(worker, thread);
            return;
        }

        int  type         = *(ushort*)(rec + AtelRecordType) & 0x1f;
        bool interpolated = type is 3 or 4;

        float before = interpolated ? *(float*)(rec + AtelRecordProgress) : 0f;

        original.fnptr!(worker, thread);

        if (interpolated) rewrite_progress(rec, before, scale);
    }

    /* The speed is scaled across the call and put back, rather than left scaled. It is not a field
     * the reader rewrites unconditionally: the accelerated move types read it back as the velocity
     * they integrate their acceleration into, so a halved value left in place would be halved again
     * every call and the approach would decay to nothing.
     *
     * The transform block is selected exactly the way the integrator selects it, because a
     * mismatch would scale a float that is not the speed. Script type 4 has no transform block at
     * all and the integrator would fault on it, so that worker is passed straight through and left
     * to the engine's own behaviour rather than guessed at. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_atel_integrate_position(nint worker, nint thread)
    {
        var original = new FhCallHandle<d_atel_worker_pass>(
            new FhCallLocation(EngineAddresses.AtelWorkerIntegratePos, 0)).chain_from(h_atel_integrate_position);

        _atel_integrator_calls++;

        float scale = Scale;
        if (scale == 1f || worker == 0)
        {
            original.fnptr!(worker, thread);
            return;
        }

        int kind = *(byte*)(worker + AtelWorkerBindKind);
        nint header = *(nint*)worker;

        if (kind is < 2 or > 5 || header == 0)
        {
            if (header == 0) _atel_declined_header_null++;
            else             note_declined(kind, *(byte*)header);
            original.fnptr!(worker, thread);
            return;
        }

        int script_type = *(byte*)header;
        nint transform = script_type == 4      ? 0
                       : script_type is 5 or 6 ? worker + AtelActorTransformSmall
                       :                         worker + AtelActorTransform;

        if (transform == 0)
        {
            note_declined(kind, script_type);
            original.fnptr!(worker, thread);
            return;
        }

        float* speed = (float*)(transform + AtelActorSpeed);
        float  saved = *speed;

        *speed = saved / scale;
        original.fnptr!(worker, thread);
        *speed = saved;

        _atel_integrator_scaled++;
    }

    /* Counted rather than logged. A pass-through happens per worker per frame, so a log line would
     * be tens of thousands of lines a minute; the census answers the same question in one field. */
    private void note_declined(int kind, int script_type)
    {
        var key = (kind, script_type);
        _atel_declined[key] = _atel_declined.TryGetValue(key, out long n) ? n + 1 : 1;
    }
}
