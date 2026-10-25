namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The vertical channel of a swimming actor: the buoyancy that lifts it towards the surface,
///     the gravity that pulls it back, the clamp that bounds the resulting velocity, and the
///     smoothed foot offset the ground clamp is measured against.
///
///     All of it lives in the second per-actor loop of Ch_CalcMain, and none of it reads the delta
///     the first loop is handed - the loop preloads its constants into the FPU before it starts and
///     never looks at the argument, which is why rewriting that delta changed nothing here. Its five
///     rates are a step per call: the buoyancy 0.32688832, the gravity 0.16344416, the surface seek
///     divided by 10, the foot smoothing divided by 30, and the velocity clamp at twice the
///     submersion depth. At 60 Hz all five run twice as often as they were authored to.
///
///     The correction is a hold rather than a conversion, and the choice is not a matter of taste.
///     The acceleration pair is standard gravity divided by 30 and by 60 exactly, so the two
///     accelerations are displacements per 30 Hz frame while the clamp is a velocity per 30 Hz
///     frame: converting them means scaling one group by the framerate factor and the other by its
///     square, on constants that are shared with the rest of the executable in three of the five
///     cases. A hold picks none of them and reproduces the engine's own 30 Hz sequence.
///
///     The state is put back after the buoyancy loop rather than staged before it, which is exact here
///     because nothing in the loop is random: all three fields are deterministic functions of their
///     previous value and of inputs the loop does not write - the ground height, the two collision
///     radii and the two water levels. Drawn height therefore goes n+1, n+2, n+2, n+3, with each
///     authored value on two presented frames.
///
///     Three things about the shape that are load-bearing:
///
///     <list type="bullet">
///     <item>The bracket starts before the whole call, not only the buoyancy loop. The swim controller
///     the first loop runs for a swimming actor ends by adding 0.15 to the same vertical velocity
///     when it is below 1.0, a stroke impulse with no clock of its own. A bracket that started
///     after the first loop would leave that impulse running at the presented rate while
///     suppressing the gravity and the clamp that answer it, and the actor would climb.</item>
///     <item>The filter on gravity mode 2 is not an optimisation. Modes 0 and 1 assign the height
///     from the ground height rather than stepping it, and the ground height is itself recomputed
///     every presented frame from the actor's new horizontal position. Restoring the height for
///     those actors would make every walking character's feet lag the terrain by a frame on every
///     slope.</item>
///     <item>The facing is deliberately left alone. The swim controller steps it towards its target
///     by a constant per call in the same loop, but facing is horizontal, and horizontal motion is
///     delta-scaled rather than held - holding it would make a swimmer turn at 30 Hz while
///     translating at 60 Hz.</item>
///     </list>
///
///     The alternative to the hold is delta-scaling the observed change, height plus the difference
///     divided by the framerate factor, which is the one variant that keeps the height smooth at
///     60 Hz. It is not exact: under constant acceleration two scaled 60 Hz steps advance a quarter
///     of an acceleration further than one 30 Hz step, about 0.08 world units, which is bounded by
///     the clamp and the surface seek but accumulates during a fall. Which of the two is right is an
///     observation nobody has made, so the exact one is what ships.
///
///     Restore after the native job wait, before character workers are dispatched. Restoring on
///     return from Ch_CalcMain races workers reading the height for their draw matrices.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>Ch work record stride. The array runs from chr for nb_maxchr records.</summary>
    private const int ChWorkRecordStride = 0x880;

    /// <summary>Ch work record, sbyte. Zero while the slot is free; every per-actor loop gates on it.</summary>
    private const int ChSlotInUseOffset = 0x2;

    /// <summary>Ch work record, float. World Y, the height the buoyancy loop integrates.</summary>
    private const int ChPosYOffset = 0x10;

    /// <summary>
    ///     Ch work record, float. The per-pass vertical displacement the move step consumes and
    ///     zeroes. Read here only to answer whether anything outside Ch_CalcMain ever arms a
    ///     vertical impulse on a swimmer.
    /// </summary>
    private const int ChVerticalStepOffset = 0x50;

    /// <summary>
    ///     Ch work record, sbyte. The gravity mode Ch_SetGravMode writes: 0 none, 1 grounded,
    ///     2 swimming. Only the buoyancy loop reads it, and only the ATEL opcode setGravityMode
    ///     ever passes 2 - 129 sites in 29 event scripts, none in battle.
    /// </summary>
    private const int ChGravityModeOffset = 0x182;

    /// <summary>Ch work record, float. The smoothed foot offset, chasing the larger of two radii by a thirtieth.</summary>
    private const int ChFootOffsetOffset = 0x4FC;

    /// <summary>Ch work record, float. Vertical velocity in world units per 30 Hz frame. Two writers in one pass.</summary>
    private const int ChVerticalVelocityOffset = 0x504;

    private struct BuoyancyState
    {
        public nint  Actor;
        public float PosY;
        public float VelY;
        public float Foot;
    }

    private bool _buoyancy_restore_ready;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_buoyancy_barrier();

    private bool init_buoyancy_hook()
        => _buoyancy_restore_ready = hook_or_log("character job barrier", EngineAddresses.CharacterJobBarrier,
            () => new FhCallHandle<d_buoyancy_barrier>(new FhCallLocation(EngineAddresses.CharacterJobBarrier, 0))
                .hook(this, h_buoyancy_barrier));

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_buoyancy_barrier()
    {
        new FhCallHandle<d_buoyancy_barrier>(new FhCallLocation(EngineAddresses.CharacterJobBarrier, 0))
            .chain_from(h_buoyancy_barrier).fnptr!();
        // Only Ch_CalcMain stages a restore; the other barrier caller must leave actor state alone.
        buoyancy_hold_end();
    }

    private BuoyancyState[] _buoyancy_saved = [];
    private int _buoyancy_saved_count;

    private long _buoyancy_actors;
    private long _buoyancy_held;
    private long _buoyancy_impulses;

    /// <summary>
    ///     Swimming actors seen, of those the ones whose vertical channel this frame put back, and
    ///     the ones that arrived with a vertical displacement already armed. Zero for almost all of
    ///     a playthrough: gravity mode 2 is reachable only from an event script, and no battle
    ///     script sets it. A non-zero third number means something outside Ch_CalcMain moves a
    ///     swimmer vertically and is its own question.
    /// </summary>
    private string buoyancy_counts()
        => $"buoyancy={_buoyancy_actors}/{_buoyancy_held}/{_buoyancy_impulses}";

    /// <summary>
    ///     Walks the actor array and, on a frame a 30 Hz game would not have taken, records the
    ///     three fields of every swimming actor. The gates the loops share are deliberately not
    ///     reproduced: an actor that fails them is written by nothing in the pass, so its restore is
    ///     a no-op, and the first loop masks the hide byte before the second loop reads it, which
    ///     makes the gate unpredictable from here anyway.
    /// </summary>
    private void buoyancy_hold_begin()
    {
        _buoyancy_saved_count = 0;
        if (!_config.Buoyancy || !_buoyancy_restore_ready) return;

        nint actors = FhUtil.get_at<nint>(EngineAddresses.ChrArray);
        int  count  = FhUtil.get_at<int>(EngineAddresses.NbMaxChr);
        if (actors == 0 || count <= 0) return;

        bool hold = !advance_this_frame();
        if (hold && _buoyancy_saved.Length < count) _buoyancy_saved = new BuoyancyState[count];

        for (int i = 0; i < count; i++)
        {
            nint actor = actors + i * ChWorkRecordStride;

            if (*(sbyte*)(actor + ChSlotInUseOffset) == 0) continue;
            if (*(sbyte*)(actor + ChGravityModeOffset) != 2) continue;

            _buoyancy_actors++;
            if (*(float*)(actor + ChVerticalStepOffset) != 0f) _buoyancy_impulses++;

            if (!hold) continue;

            _buoyancy_saved[_buoyancy_saved_count++] = new BuoyancyState
            {
                Actor = actor,
                PosY  = *(float*)(actor + ChPosYOffset),
                VelY  = *(float*)(actor + ChVerticalVelocityOffset),
                Foot  = *(float*)(actor + ChFootOffsetOffset),
            };

            _buoyancy_held++;
        }
    }

    /// <summary>
    ///     Puts the three fields back. The above-water flag and the height delta the pass also wrote
    ///     stay as they are: both are recomputed from the height every call rather than accumulated,
    ///     and the flag has a reader inside the pass that picks the walkmesh polygon filter for a
    ///     swimmer from it.
    /// </summary>
    private void buoyancy_hold_end()
    {
        for (int i = 0; i < _buoyancy_saved_count; i++)
        {
            ref BuoyancyState saved = ref _buoyancy_saved[i];

            *(float*)(saved.Actor + ChPosYOffset)             = saved.PosY;
            *(float*)(saved.Actor + ChVerticalVelocityOffset) = saved.VelY;
            *(float*)(saved.Actor + ChFootOffsetOffset)       = saved.Foot;
        }

        _buoyancy_saved_count = 0;
    }
}
