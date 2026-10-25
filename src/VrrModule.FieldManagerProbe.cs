namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     A read-only probe over the field particle manager array, for the one diagnosis that cannot be
///     made from the counters the other subsystems keep.
///
///     Field particle groups vanish and come back, and two candidate causes are left. pppFpLoop
///     keeps a restart countdown at manager+0x00 which it decrements once per pass, with no time
///     term at all, and then tests the result: while the result is still zero or above the group
///     is skipped outright - not advanced, not drawn - and at -1 every object is freed and the
///     group is started again in the same pass as the decrement. That is a pass-counted duration,
///     so at twice the pass rate it elapses in half the wall clock time. The other candidate is
///     pppCheckViewCylinder, which is vanilla, camera driven and rate independent, and which
///     nothing in this module could have changed.
///
///     The countdown is what this reads, and the two end-condition flags beside it are what says
///     which kind of group is being looked at: _pppRunPartFp ends a group on its authored death
///     time when +0x1C is zero, and on an empty object list when +0x1C and +0x1D are both set. A
///     session in which every live manager reads -1 throughout eliminates the countdown and leaves
///     the view cylinder.
///
///     Draw-nothing, write-nothing. It reads engine memory and emits a log line; it holds no frame,
///     scales no step, and no correction reads any state it keeps.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const int FieldManagerStride = 0x80;

    /// <summary>
    ///     Entries in the manager array. The parallel per-group array pppFpLoop indexes alongside it
    ///     begins 0x4000 bytes further on, so the table cannot hold more than this however the count
    ///     word reads.
    /// </summary>
    private const int FieldManagerCapacity = 0x4000 / FieldManagerStride;

    /// <summary>pppFpLoop's own marker for a group that is not running.</summary>
    private const int FieldManagerDead = unchecked((int)0xFFFFF000);

    private TimeSpan _fp_probe_last;
    private long _fp_countdowns_seen;

    /// <summary>Last sampled age per group, keyed by group index, with the object it was read from.</summary>
    private readonly Dictionary<int, (nint Obj, int Age)> _fp_ages = [];

    /// <summary>
    ///     A group's standing population, which is the number "there are more of them than before" is
    ///     a claim about and the one number no existing counter carries.
    ///
    ///     The engine's own <c>pobjcounter</c> cannot answer it from outside: the battle particle loop
    ///     writes it to zero at its head every frame and the field pass only adds to it, so a sampler
    ///     on the present hook reads whatever is left after the reset, which is why pobj has read
    ///     0/max0 in every run. Its mirror is written only inside the debug overlay branch.
    ///
    ///     The manager carries the count directly instead. <c>mgr+0x18</c> is the number of emitter
    ///     entries and <c>mgr+0x3c</c> the array of them, 0x10 bytes each, and each entry keeps its
    ///     own live object count as a ushort at <c>+0xc</c> - maintained by pppCreatePObject and
    ///     pppDeletePObject, so it is exact rather than sampled. Summing it is the same walk
    ///     FUN_00712c60 and FUN_007170f0 make.
    /// </summary>
    private static int live_objects(byte* mgr)
    {
        int emitters = *(int*)(mgr + 0x18);
        nint array   = *(nint*)(mgr + 0x3c);

        if (array == 0 || emitters <= 0) return 0;

        int objects = 0;
        for (int e = 0; e < emitters; e++) objects += *(ushort*)(array + e * 0x10 + 0xc);

        return objects;
    }

    /// <summary>
    ///     Samples the array once a second from the frame hook, which is the game thread and is
    ///     between two pppFpLoop passes rather than inside one, so nothing is being written while it
    ///     reads. One line per sample rather than one per manager, so a play session stays readable.
    /// </summary>
    private void sample_field_managers()
    {
        if (!_config.FieldManagerProbe) return;

        TimeSpan now = _clock.Elapsed;
        if ((now - _fp_probe_last).TotalSeconds < 1) return;
        _fp_probe_last = now;

        nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
        if (table == 0) return;

        int count = *(ushort*)(table + 4);
        if (count > FieldManagerCapacity) count = FieldManagerCapacity;

        byte* managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);

        List<string> live = [];
        int total_objects = 0;

        for (int i = 0; i < count; i++)
        {
            byte* mgr = managers + i * FieldManagerStride;
            int countdown = *(int*)mgr;

            if (countdown == FieldManagerDead) continue;

            if (countdown >= 0) _fp_countdowns_seen++;

            // The manager's own clock. _pppRunPartFp adds the step at +0x10 onto the accumulator at
            // +0x8 once per pass, an emitter spawns when its authored due time is reached by that
            // accumulator, and the group ends when the accumulator reaches the duration at +0x4. It
            // is the emission and group-end clock only: no kernel reads it, and an object's own
            // lifetime runs on the age below instead. A step still at the authored 0x1000 therefore
            // means this group's emission schedule and its end are running at twice wall-clock speed
            // against objects that age correctly.
            //
            // The age of this group's first live object and how far it moved since the last sample.
            // The timeline patch writes 0x800 into the two instructions that age an object once per
            // pass, and the field pass runs once per presented frame, so a correct object gains
            // 60 * 0x800 = 0x1E000 per second. Half that is an object living twice as long as
            // authored; double it is one dying in half the time. The reading is only meaningful
            // while the object identity holds, which d=new marks.
            nint first = *(nint*)(mgr + 0x30);
            string age = "age=-";

            if (first != 0)
            {
                int now_age = *(int*)(first + 0xC);
                age = _fp_ages.TryGetValue(i, out (nint Obj, int Age) was) && was.Obj == first
                    ? $"age=0x{now_age:X} d=0x{now_age - was.Age:X}"
                    : $"age=0x{now_age:X} d=new";

                _fp_ages[i] = (first, now_age);
            }
            else _fp_ages.Remove(i);

            int objects = live_objects(mgr);
            total_objects += objects;

            live.Add($"[{i} cd={countdown} f1c={mgr[0x1C]} f1d={mgr[0x1D]} " +
                     $"step=0x{*(int*)(mgr + 0x10):X} acc=0x{*(int*)(mgr + 0x08):X} dur=0x{*(int*)(mgr + 0x04):X} " +
                     $"emit={*(int*)(mgr + 0x18)} obj={objects} {age}]");
        }

        // Logged even when nothing is live: an empty sample is what says the field half was reached
        // at all, and distinguishes a scene with no groups from a probe that never ran.
        _logger.Info($"[Vrr] fp_mgr t={ElapsedSeconds:F1} groups={count} live={live.Count} " +
                     $"obj_total={total_objects} counting={_fp_countdowns_seen} {string.Join(' ', live)}");
    }
}
