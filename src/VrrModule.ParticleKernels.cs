namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Particle retiming at the only place the engine separates advancing from drawing.
///
///     A particle program is a list of steps, and each step's descriptor carries up to two
///     per-object entry points: <c>+0x8</c> and <c>+0xc</c>. The dispatch in pppRunPartStd and
///     FUN_007170f0 calls whichever are present, once per object. The two are not variants of the
///     same thing - pppKeThSft adds constants into the object's position, angle and colour and
///     touches nothing else, while pppKeThDraw only reads the object and calls KeThCp_Draw. Update
///     and draw, in that order.
///
///     That is what makes this the one retiming that can work. Holding the pass, at any level above
///     this, takes the draw with it and the particles vanish on held frames - measured, twice.
///     Holding the update kernels alone leaves every draw in place: the objects simply do not move
///     on that frame, which is exactly what a 30 Hz animation looks like at 60 Hz.
///
///     The motion itself carries no time: the kernels add a constant per call. So there is nothing
///     to scale, and halving the manager's step reaches only the emission and lifetime timeline.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     The step kernels that mutate an object, by RVA, and whether holding one is safe.
    ///     Everything ending in Con, Con2, Des or Draw is absent: those construct a step or draw
    ///     one, and neither advances anything.
    ///
    ///     <c>Hold: false</c> also marks pppKeDrct and pppKeGrvTgt, whose entire body is the
    ///     keyframe one-shot. They do nothing on an ordinary tick, so holding them retimes nothing
    ///     and can only cost the keyframe. They stay hooked for their counts.
    ///
    ///     <c>Hold: false</c> marks the four the Lns draw kernels call themselves -
    ///     <c>pppKeLnsFlsDraw</c> opens with <c>pppKeLnsFlsUpdate(param_1, param_2, param_3)</c>,
    ///     and Arnd, Clm and Crn do the same. Holding them is not risky, it is pointless: their
    ///     entire body sits inside the keyframe test, so on an ordinary tick they do nothing at
    ///     all, and on a keyframe tick they resolve the shape pointer and seed the sprite
    ///     accumulator the draw reads back immediately. Skipping that leaves the draw with the
    ///     init stub's placeholder pointer, which its own guard then turns into an invisible or
    ///     wrongly bright sprite - which is what a running game showed.
    ///
    ///     No time passes in them. The clock of this family is <c>KeLnsShp_Update</c>, which the
    ///     draw calls and which advances a fixed step per drawn frame; that is scaled instead, in
    ///     <see cref="h_lens_shape_update"/>.
    /// </summary>
    private static readonly (string Name, nint Rva, bool Hold)[] KernelUpdates =
    [
        ("pppKeBornRnd2",      0x3587B0, true ), ("pppKeBornRnd3",      0x358960, true ),
        ("pppKeBornRnd5",      0x358F20, true ), ("pppKeBornRnd6",      0x359200, true ),
        ("pppKeDrct",          0x35E430, false), ("pppKeGrvEff",        0x359650, true ),
        ("pppKeGrvTgt",        0x3597B0, false), ("pppKeHmgEff",        0x359BF0, true ),
        ("pppKeLnsArndUpdate", 0x35A2D0, false), ("pppKeLnsClmUpdate",  0x35A6A0, false),
        ("pppKeLnsCrnUpdate",  0x35AAB0, false), ("pppKeLnsFlsUpdate",  0x35AE90, false),
        ("pppKeLnsLpSft",      0x359F30, true ),
        ("pppKeMvYpEff",       0x35E310, false), ("pppKeShpTail",       0x34C490, true ),
        ("pppKeShpTail2",      0x34E490, true ), ("pppKeShpTail2X",     0x34F4D0, true ),
        ("pppKeShpTail3",      0x3502C0, true ), ("pppKeShpTail3X",     0x351CA0, true ),
        ("pppKeShpTailX",      0x34D490, true ),
        ("pppKeThSft",         0x336E90, true ), ("pppKeThTp",          0x336D80, false),

        // The generic integrators, and the reason a pyrefly grew into a fireball. They are not
        // Ke-prefixed and were never on this list, but they sit in prog_func exactly like the
        // others, run once per pass, and integrate a constant per call.
        //
        // pppSclAccele and pppColAccele are double integrators - rate += acceleration, value +=
        // rate - so at twice the call rate their error grows with the square of time rather than
        // linearly. Scale and colour are precisely what the defect showed. They were always running
        // at double rate; halving the object timeline only let the objects live long enough for the
        // divergence to become visible.
        ("pppMove",            0x35BDE0, true ), ("pppAccele",          0x35B740, true ),
        ("pppAngMove",         0x35BEF0, true ), ("pppAngAccele",       0x35B850, true ),
        ("pppSclMove",         0x35BFA0, true ), ("pppSclAccele",       0x35B900, true ),
        ("pppColMove",         0x35C390, true ), ("pppColAccele",       0x35BA40, true ),

        // The spawners, and the reason a corrected scene holds more particles than an uncorrected
        // one. Each of these creates objects on a schedule counted in passes and in nothing else:
        // the Ap family keeps a countdown at obj+0xa2, decrements it once at the tail of every call
        // and, at zero, spawns the step's authored batch and re-arms from the step data;
        // pppKeBornRnd has no counter at all and spawns on a random gate evaluated per call. At
        // 60 Hz all of them therefore emit at twice the authored rate.
        //
        // That was invisible while nothing else was corrected, because objects also aged twice as
        // fast and the population balanced. Halving the age step ends the cancellation: births stay
        // doubled while deaths return to their authored wall clock, and the standing population
        // doubles until it pins at the authored maximum. Measured in run 20260912_084409, at the
        // instant the age step was written: group 1 went from 34 objects to 64 and pinned there,
        // groups 7 to 11 from 5 to 13, and obj_total from 83 to 254. The pass-counted ones are the
        // same hazard as the field group restart countdown, one level further down.
        //
        // pppVertexAp alone is 2,047 uses across the shipped field data, against 207 for the
        // busiest kernel already on this list. pppFaceAp is the seventh member of the family and is
        // absent deliberately: no shipped room uses it, and the address the table gives for it does
        // not resolve to a function in the decompilation.
        ("pppVertexAp",        0x357840, true ), ("pppVertexApLc",      0x357FF0, true ),
        ("pppVertexApDisPos",  0x357C30, true ), ("pppVertexApAt",      0x3582E0, true ),
        ("pppPointAp",         0x3573C0, true ), ("pppKeBornRnd",       0x3584D0, true ),
    ];

    /// <summary>
    ///     The object's current program step, at <c>+0xc</c>. Every kernel compares it against the
    ///     step index it was given: pppKeThSft runs its body only when they match, and
    ///     pppKeShpTail3 treats zero as "this object is new" and seeds all 28 entries of its trail
    ///     ring from the current position.
    ///
    ///     A held frame must never be an object's first frame. Skipping the first call leaves that
    ///     seeding undone, and the draw kernel then reads a ring that was never written - which is
    ///     what threw the pyreflies across the screen.
    /// </summary>
    private const int ObjectStepOffset = 0xc;

    /// <summary>
    ///     True on a call the object cannot afford to miss, whatever else the kernel does.
    ///
    ///     <c>obj+0xc</c> is not a step index but the object's age in fixed point, advanced by
    ///     0x1000 per pass at the tail of the dispatcher. A step's parameters arrive as a keyframe
    ///     timeline, and a kernel runs its one-shot arm exactly when the age matches the keyframe
    ///     the dispatcher's cursor is on - <c>*param_2</c>, already in the arguments. The cursor
    ///     moves on afterwards, so that tick never comes back.
    ///
    ///     Three kernels resolve a pointer to another emitter's object on that tick, cache it in
    ///     their working area and dereference it unconditionally below: pppKeGrvEff and pppKeHmgEff
    ///     write it to <c>work+0xa0</c> and <c>work+0xa4</c>, pppKeTh likewise. Their constructors
    ///     are empty stubs and pppCreatePObject does not clear the working area, so a lost keyframe
    ///     leaves them dereferencing recycled heap rather than merely animating wrong. Age zero is
    ///     covered by the same test because the first pass is a keyframe too.
    /// </summary>
    private static bool is_initialising_call(int obj, int data)
    {
        int age = *(int*)(obj + ObjectStepOffset);
        return age == 0 || age == *(int*)data;
    }

    /// <summary>
    ///     Whether this object has advanced far enough to deserve another update.
    ///
    ///     Deciding that from a frame counter was wrong once the age step was halved, and wrong in a
    ///     way that got worse the longer an effect lived. Both patterns are then two-beat - the
    ///     counter skips every other call, the age crosses a vanilla step every other pass - and
    ///     their phases are unrelated. Where they line up badly the hold lands on exactly the passes
    ///     that matter: a trail kernel writes its ring buffer at an index derived from the age, so
    ///     the slots it never gets to write keep whatever was in them, and the trail fills with stale
    ///     positions. That is a pyrefly that starts correct and degrades.
    ///
    ///     The age is the honest clock here. Running on the vanilla grid gives each object exactly
    ///     the cadence it was authored for, in its own phase rather than the frame counter's, and it
    ///     contains every keyframe by construction.
    ///
    ///     An object whose age is not on the grid at all - which a loop point that is not a multiple
    ///     of the step would produce - is never held, because freezing it outright is worse than
    ///     running it too often.
    /// </summary>
    private bool object_may_advance(int obj)
    {
        int step = _timeline_step;
        if (step <= 0 || step >= VanillaAgeStep) return true;

        int age = *(int*)(obj + ObjectStepOffset);

        // An age off the grid has no phase to be in, which a loop point that is not a multiple of
        // the step produces. Falling through to the frame counter keeps such an object retimed at
        // the right average rate; returning true would exempt it from the hold entirely, and
        // measured 2026-09-01 that is what happened - ke_held stopped rising through twenty
        // thousand further calls while the timeline kept running at half speed. An escape hatch
        // that becomes the common case silently disables the correction.
        if (age < 0 || age % step != 0)
        {
            _offgrid_ages++;
            return advance_this_frame();
        }

        return age % VanillaAgeStep == 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ke_update(int obj, int data, int prog);

    // The chain is keyed by delegate, so every target needs its own instance; the list is also what
    // keeps them alive against the collector.
    private readonly List<d_ke_update> _kernel_hooks = [];

    private long _ke_calls;
    private long _ke_held;
    private long _offgrid_ages;

    // Per kernel, so a scene can be asked which steps it actually runs rather than guessed at.
    private readonly Dictionary<string, (long Calls, long Held)> _ke_by_name = [];

    private string kernel_counts()
    {
        string busiest = string.Join(' ', _ke_by_name
            .Where(p => p.Value.Calls > 0)
            .OrderByDescending(p => p.Value.Calls)
            .Take(6)
            .Select(p => $"{p.Key}={p.Value.Calls}/{p.Value.Held}"));

        return $"ke_calls={_ke_calls} ke_held={_ke_held} ke_offgrid={_offgrid_ages}"
             + (busiest.Length > 0 ? $" [{busiest}]" : "");
    }

    private bool init_particle_kernel_hooks()
    {
        if (!_config.ParticleKernelHold && !_config.ParticleIntegratorScale) return true;

        _logger.Warning("Particle kernel retiming is disabled for pppKeMatSN (RVA 0x334020) and pppKeTh (RVA 0x336050): the current aligned-stack ABI and hold contract are not verified.");
        bool ok = true;

        foreach ((string name, nint rva, bool safe) in KernelUpdates)
        {
            // Every kernel is hooked so its call count is known; only the selected ones are held.
            // A scene's own list of steps is the thing worth knowing, and it costs one counter.
            bool hold = _config.ParticleKernelHold && safe
                        && (_config.ParticleKernelOnly.Length == 0
                            || _config.ParticleKernelOnly.Contains(name, StringComparer.OrdinalIgnoreCase))
                        && !_config.ParticleKernelExcept.Contains(name, StringComparer.OrdinalIgnoreCase);

            string key = name;
            _ke_by_name[key] = (0, 0);

            // An integrator is retimed by scaling what it adds rather than by skipping it, which is
            // the same rate with a valid value on every frame. It then never takes the hold path.
            // Only the float integrators by default: their halved delta is exact, the integer ones
            // alternate a remainder that is only correct at Scale 2 and drive alpha where a wrong
            // rounding is visible rather than merely imprecise.
            IntegratorWidth? width = _config.ParticleIntegratorScale ? integrator_width(name) : null;
            if (width is not IntegratorWidth.Float32 && !_config.ParticleIntegratorScaleIntegers) width = null;
            if (width is not null) hold = false;

            d_ke_update? self = null;

            self = (obj, data, prog) =>
            {
                _ke_calls++;
                note_keyframe_grid(data);
                var counts = _ke_by_name[key];

                if (width is IntegratorWidth w)
                {
                    _ke_by_name[key] = (counts.Calls + 1, counts.Held);
                    run_integrator_scaled(rva, self!, obj, data, prog, w);
                    return;
                }

                // The age decides while the timeline patch is active, because only it is in phase
                // with the object; without that patch there is no grid to be in phase with and the
                // frame counter is all there is.
                bool may_advance = _config.ParticleTimeline ? object_may_advance(obj) : advance_this_frame();

                if (hold && !may_advance && !is_initialising_call(obj, data))
                {
                    _ke_held++;
                    _ke_by_name[key] = (counts.Calls + 1, counts.Held + 1);
                    return;
                }

                _ke_by_name[key] = (counts.Calls + 1, counts.Held);

                new FhCallHandle<d_ke_update>(new FhCallLocation(rva, 0))
                    .chain_from(self!).fnptr?.Invoke(obj, data, prog);
            };

            _kernel_hooks.Add(self);

            d_ke_update hook = self;
            ok &= hook_or_log(HookSiteId.ParticleStepKernelTemplate, name, rva,
                () => new FhCallHandle<d_ke_update>(new FhCallLocation(rva, 0)).hook(this, hook));

            _logger.Info(hold
                ? $"[Vrr] Holding particle step kernel {name} at RVA 0x{rva:X}."
                : $"[Vrr] Counting particle step kernel {name} at RVA 0x{rva:X}, not held.");
        }

        return ok;
    }
}
