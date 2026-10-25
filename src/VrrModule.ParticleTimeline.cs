namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The particle system's own clock, and the only correction here that is a rate change in the
///     image rather than a hook.
///
///     Every particle object carries its age in fixed point at obj+0xc, and the dispatcher advances
///     it by a constant per pass. That age is the timeline: a step's keyframe fires when the age
///     equals the cursor's tag, the loop point and the death threshold are compared against it, and
///     the trail kernels index their history buffer with it. At 60 Hz it advances twice as fast as
///     it was authored for, which is why a campfire burns through its program in half the time.
///
///     Halving the constant is safe in a way a hold never is, and the reason is arithmetic rather
///     than luck. A keyframe fires on exact equality, so every tag that works today must lie in
///     {base + k * 0x1000}; halving makes the reachable set {base + k * 0x800}, a strict superset.
///     No keyframe can be missed. Everything else expressed in age units - the loop point, the death
///     threshold, the trail's "am I in the last pass" test, the negative age nudge that keeps an
///     object alive one more pass - is authored data in the same unit and stays correct in wall
///     clock time without being touched. Patching those "for consistency" would break them.
///
///     The manager's emission clock has to move with it. It advances by its own step per pass and
///     decides when the next object is spawned, so halving only the age would double the standing
///     population: the same spawn rate per frame against twice as many frames of life.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     The two instructions that convert passes into age. Found by reading the dispatcher and
    ///     confirmed by scanning the whole image, every shipped DLL and every executable in the game
    ///     directory: there are exactly two, both in FFX.exe, and no overlay carries a third.
    /// </summary>
    private static readonly (string Name, nint Rva)[] AgeStepSites =
    [
        ("pppRunPartStd", 0x316D42),
        ("FUN_007170f0",  0x3171A2),
    ];

    /// <summary>`add dword ptr [eax+0Ch], imm32` - the immediate starts at the fourth byte.</summary>
    private static ReadOnlySpan<byte> AgeStepPrefix => [0x81, 0x40, 0x0C];
    private const int AgeStepImmediateOffset = 3;
    private const int VanillaAgeStep = 0x1000;

    private long _offgrid_keyframes;

    /// <summary>
    ///     The engine's own count of live particle objects, accumulated during the dispatcher pass
    ///     and reset each frame. Sampling it from outside catches a zero almost every time, so it is
    ///     sampled here once per presented frame and reported as a range.
    ///
    ///     This is what separates the two things "the effect escalates" can mean: more objects than
    ///     vanilla, which is an emission problem, or the same objects drawn wrongly, which is not.
    /// </summary>
    private int _pobj_max;
    private int _pobj_last;

    private void sample_particle_population()
    {
        int live = FhUtil.get_at<int>(EngineAddresses.PobjCounter);
        if (live <= 0) return;

        _pobj_last = live;
        if (live > _pobj_max) _pobj_max = live;
    }

    private string particle_timeline_counts()
    {
        string counts = $"kf_offgrid={_offgrid_keyframes} pobj={_pobj_last}/max{_pobj_max} " +
                        $"age_step=0x{_timeline_step:X}";

        // The peak is per telemetry window, so a population that grows over a minute shows as a
        // rising sequence of maxima rather than one number that has already saturated.
        _pobj_max = 0;
        return counts;
    }

    private bool _timeline_transition_refused;

    private bool timeline_has_live_objects()
    {
        byte* field = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        for (int i = 0; i < field_manager_count(); i++)
            if (live_objects(field + i * ParticleManagerStride) > 0) return true;

        byte* battle = FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers);
        int count = FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount);
        for (int i = 0; i < count; i++)
            if (live_objects(battle + i * ParticleManagerStride) > 0) return true;
        return false;
    }
    private readonly Dictionary<nint, (int Authored, int Applied)> _timeline_manager_steps = [];

    private int _timeline_step;
    private bool _timeline_divisor_refused;

    /// <summary>
    ///     Called once per presented frame from <c>apply_rate_patches</c>, and a compare and a return
    ///     unless the step it derives has changed. Never before a rate has been adopted: at init the
    ///     rate is only implied by the limiter, and with the limiter removed that implication is
    ///     wrong.
    ///
    ///     A one-shot patch looked right and was not: the first measurement of a run happens while
    ///     the game is still loading, and a boot sample of 46.7 fps froze the step at a correction
    ///     for 50 Hz that the next sample already knew was wrong. Writing again is safe because the
    ///     journal restores in reverse order, so the original still comes back last.
    ///
    ///     Scale is the right divisor here, and that is a statement about the age step rather than a
    ///     convention: pppPartLoop runs in the draw half of every Sg_MainLoop pass, behind none of
    ///     the frame-skip render gates, so a particle's age advances once per simulation pass and not
    ///     once per presented frame. Passes per authored 30 Hz step is what Scale is.
    /// </summary>
    private void patch_particle_timeline()
    {
        if (!_config.ParticleTimeline) return;

        // The step must divide the vanilla one exactly, and that is not a preference.
        //
        // The whole safety argument is that the halved age sequence contains every value the old one
        // reached, so no keyframe can be missed - and that only holds for 0x1000 / n. A step of
        // 0x99A, which is what a scale of 1.67 asks for, makes the ages multiples of 2458 and they
        // then never equal a keyframe at a multiple of 4096 again. The effect keeps its rate and
        // loses its program: pyreflies that stop at whatever size their last reached keyframe left
        // them at, and a fire that burns at the right speed and looks wrong.
        int divisor = Math.Max(1, (int)Math.Round(Scale));

        // A divisor that does not divide the vanilla step exactly is the failure the paragraph above
        // describes, and rounding the rate to a whole multiple of 30 does not prevent it: 90 Hz
        // rounds to 3, and 0x1000 / 3 is 1365, whose multiples reach 4095 and then step past 4096.
        // Every keyframe would be missed from the first loop onwards. Refusing is the only correct
        // answer, because there is no third step that both divides 0x1000 and matches the rate.
        if (Math.Abs(Scale - divisor) > 0.01f || VanillaAgeStep % divisor != 0)
        {
            // Reported once: this is decided per frame and the rate does not change back on its own.
            // A step already written stays written - it is a valid one, and the alternative is
            // leaving the timeline patched with nothing at all.
            if (!_timeline_divisor_refused)
            {
                _timeline_divisor_refused = true;
                _logger.Error($"[Vrr] Particle timeline refuses scale {Scale:F3}: it must match an integer divisor of 0x{VanillaAgeStep:X}. The previous age/manager clock is retained; the requested rate is not fully corrected.");
            }

            return;
        }

        _timeline_divisor_refused = false;
        int step = VanillaAgeStep / divisor;

        if (step == _timeline_step) return;
        // A larger step can jump past an equality keyframe in an existing object's finer phase.
        // Wait for empty manager lists; changing the rate is unsupported until those objects die.
        if (_timeline_step > 0 && step > _timeline_step && timeline_has_live_objects())
        {
            if (!_timeline_transition_refused)
                _logger.Error("[Vrr] Particle rate transition deferred while live objects retain a finer keyframe phase. The requested rate is not fully corrected.");
            _timeline_transition_refused = true;
            return;
        }
        _timeline_transition_refused = false;

        foreach ((string name, nint rva) in AgeStepSites)
        {
            byte* site = FhUtil.ptr_at<byte>(rva);

            // Verified before it is written, because a wrong address here rewrites an instruction
            // rather than failing: the three opcode bytes and the vanilla immediate must both match.
            int current = *(int*)(site + AgeStepImmediateOffset);

            // Either untouched or carrying a step this module wrote earlier; anything else is not
            // the instruction we think it is.
            bool matches = new ReadOnlySpan<byte>(site, 3).SequenceEqual(AgeStepPrefix)
                        && current > 0 && VanillaAgeStep % current == 0;

            if (!matches)
            {
                _logger.Error($"[Vrr] Particle timeline site {name} at RVA 0x{rva:X} does not carry " +
                              $"the expected instruction; nothing written. Found " +
                              $"{site[0]:X2} {site[1]:X2} {site[2]:X2} imm=0x{*(int*)(site + AgeStepImmediateOffset):X}.");
                return;
            }
        }

        foreach ((string name, nint rva) in AgeStepSites)
        {
            _patches.Write(rva + AgeStepImmediateOffset, BitConverter.GetBytes(step));
            _logger.Info($"[Vrr] Particle age step at {name} (RVA 0x{rva:X}) set to 0x{step:X}.");
        }

        _timeline_step = step;
        retime_running_managers(step);
    }

    /// <summary>
    ///     Updates emission clocks from their retained authored steps. A manager start replaces the
    ///     tracked state, including slot reuse. Untracked custom rates and engine-overwritten steps
    ///     remain untouched. The object-age phase guard must pass before this sweep runs.
    /// </summary>
    private void retime_running_managers(int step)
    {
        int fp = retime_manager_array(FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers), field_manager_count(), step);

        int count = FhUtil.get_at<int>(EngineAddresses.PpvPartManagerCount);
        int part = count <= 0 ? 0 : retime_manager_array(FhUtil.ptr_at<byte>(EngineAddresses.PpvPartManagers), count, step);

        if (fp + part > 0)
            _logger.Info($"[Vrr] Manager time step set to 0x{step:X} on {fp} field and {part} battle managers " +
                         $"that were already running.");
    }

    /// <summary>
    ///     The live entry count behind the field group table, or zero before field data has loaded.
    ///     The table pointer is a double indirection and is null until then; the manager array itself
    ///     is static storage, so a capped count can never walk off anything mapped.
    /// </summary>
    private int field_manager_count()
    {
        nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
        if (table == 0) return 0;

        int count = *(ushort*)(table + 4);
        return count > FieldManagerCapacity ? FieldManagerCapacity : count;
    }

    private int retime_manager_array(byte* managers, int count, int step)
    {
        int changed = 0;

        for (int i = 0; i < count; i++)
        {
            int* mgr_step = (int*)(managers + i * ParticleManagerStride + ParticleStepOffset);

            nint manager = (nint)(managers + i * ParticleManagerStride);
            int authored = VanillaAgeStep;
            if (_timeline_manager_steps.TryGetValue(manager, out var tracked))
            {
                if (*mgr_step != tracked.Applied) continue;
                authored = tracked.Authored;
            }
            else if (*mgr_step != VanillaAgeStep) continue;

            if (authored <= 0) continue;

            int applied = Math.Max(1, (int)((long)authored * step / VanillaAgeStep));
            *mgr_step = applied;
            _timeline_manager_steps[manager] = (authored, applied);
            changed++;
        }

        return changed;
    }

    /// <summary>
    ///     Counts keyframe tags that are not multiples of the vanilla step.
    ///
    ///     Such a tag is unreachable today, because the age only ever lands on multiples of 0x1000
    ///     and the comparison is equality - so it is dead authoring data. After the step is halved it
    ///     becomes reachable and fires for the first time. The expectation is zero; a non-zero count
    ///     is not a fault but something to look at before trusting the change.
    /// </summary>
    private void note_keyframe_grid(int data)
    {
        if (data == 0) return;
        if ((*(int*)data & (VanillaAgeStep - 1)) != 0) _offgrid_keyframes++;
    }
}
