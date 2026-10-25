namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The field particle group restart countdown, which is a pass count with no time term.
///
///     pppFpLoop keeps one at each manager's +0x00 and decrements it once per pass before testing
///     the result. While the result is still zero or above the group is skipped outright - not
///     advanced and not drawn; at -1 every live object is freed and the group is started again
///     through _pppStartPart, in the same pass as the decrement. So the
///     stretch during which the group is absent, and the instant it comes back, are both measured in
///     passes. At twice the pass rate both happen in half the authored wall time, which is the
///     vanishing and popping in that field particles show at 60 Hz.
///
///     Two things arm it. pppSetFpPdt writes the group record's +0x30 into it when field data loads,
///     which is the staggered start; the five id-addressed calls (FUN_00729620, FUN_007297f0,
///     FUN_00729830, FUN_007298d0, FUN_00729900) write a plain 0, which is a restart latch that
///     fires on the next processed pass. FUN_007297f0 is on the ordinary gameplay path that attaches
///     a group to a character, so the countdown is re-armed during play and is not a load-time-only
///     concern.
///
///     The correction adjusts the counter and never gates the call. Gating is not available here:
///     pppFpLoop measures its draw packet as ppvCurPrimp - DAT_00cec210, the distance the write
///     pointer travelled during the loop, so the draw is built inside the advance and skipping any
///     part of it drops that group's contribution to the frame. Instead, on a frame this module
///     holds, one is added back to every armed countdown before the engine's own decrement runs, so
///     the pass nets to zero and the countdown advances once per authored 30 Hz step.
///
///     The add is undone again for any manager the engine did not in fact decrement. A group is only
///     decremented on the pass that reaches LAB_00729d1f, which needs it to be alive, not marked
///     display-off, and either carrying a non-zero fprim id or passing pppCheckViewCylinder - and
///     pppFpLoop returns before its loop entirely while g_FpLoopFlag is clear. Without the undo, a
///     group that is off camera while its countdown is armed would gain one per held frame for as
///     long as the camera looks away, and would then take far longer to appear than it was authored
///     to. Comparing against the exact value written is what makes that impossible: every other
///     writer of the field stores -1, 0 or 0xFFFFF000, none of which can equal it.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>No add is outstanding for this manager on the current pass.</summary>
    private const int FieldRestartNotAdjusted = int.MinValue;

    /// <summary>The value written into each manager's countdown this pass, per manager index.</summary>
    private readonly int[] _fp_restart_written = new int[FieldManagerCapacity];

    /// <summary>Which manager indices have ever been adjusted, for the distinct count.</summary>
    private readonly bool[] _fp_restart_seen = new bool[FieldManagerCapacity];

    private long _fp_restart_held;
    private long _fp_restart_undone;
    private int  _fp_restart_managers;

    private bool init_field_particle_restart_hook()
    {
        if (!_config.FieldParticleRestartHold && !_config.VrrEnabled) return true;

        return hook_or_log("pppFpLoop", EngineAddresses.PppFpLoop,
            () => new FhCallHandle<d_ppp_fp_loop>(new FhCallLocation(EngineAddresses.PppFpLoop, 0)).hook(this, h_ppp_fp_loop));
    }

    /// <summary>
    ///     Whether the correction is reached at all: how many adjustments the engine consumed, over
    ///     how many distinct managers, and how many had to be taken back because that manager was
    ///     not stepped on the pass.
    /// </summary>
    private string field_restart_counts()
        => $"fp_cd_held={_fp_restart_held} fp_cd_mgrs={_fp_restart_managers} fp_cd_undone={_fp_restart_undone}";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ppp_fp_loop();

    /* The add goes before the chain so the engine's own decrement consumes it in the same pass. It
     * cannot go after: a countdown that stood at 0 reaches -1 inside the loop, and by then the group
     * has already been freed and restarted, so there is nothing left to hold. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ppp_fp_loop()
    {
        bool held = (RenderPassContext.IsPresentation || (_config.FieldParticleRestartHold && !advance_this_frame())) && arm_restart_hold();
        var saved = field_particle_save_manager_work();
        try
        {
            new FhCallHandle<d_ppp_fp_loop>(new FhCallLocation(EngineAddresses.PppFpLoop, 0))
                .chain_from(h_ppp_fp_loop).fnptr?.Invoke();
        }
        finally
        {
            if (held) settle_restart_hold();
            field_particle_restore_manager_work(saved);
        }
    }

    /// <summary>
    ///     Adds one to every armed countdown, and records what was written so the pass can be
    ///     settled afterwards. Returns whether anything was adjusted.
    /// </summary>
    private bool arm_restart_hold()
    {
        Array.Fill(_fp_restart_written, FieldRestartNotAdjusted);

        // The table pointer is zero until field data has been loaded, and the count lives behind it,
        // so this is a double indirection. The manager array itself is static storage, which is why
        // a capped count can never walk off anything mapped.
        nint table = FhUtil.get_at<nint>(EngineAddresses.PpvFpGroupTable);
        if (table == 0) return false;

        int count = *(ushort*)(table + 4);
        if (count > FieldManagerCapacity) count = FieldManagerCapacity;

        byte*  managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);
        bool   adjusted = false;

        for (int i = 0; i < count; i++)
        {
            int* countdown = (int*)(managers + i * FieldManagerStride);

            // Negative is a group that is running (-1) or dead (0xFFFFF000), and neither carries a
            // pending restart. int.MaxValue is excluded so the add can never turn a countdown into a
            // negative, which is a value class the engine reads as a state rather than as a count.
            if (*countdown == int.MaxValue && RenderPassContext.IsPresentation)
            {
                _fp_restart_written[i] = int.MaxValue;
                adjusted = true;
                continue;
            }
            if (*countdown < 0 || *countdown == int.MaxValue) continue;

            *countdown += 1;
            _fp_restart_written[i] = *countdown;
            adjusted = true;

            if (!_fp_restart_seen[i])
            {
                _fp_restart_seen[i] = true;
                _fp_restart_managers++;
            }
        }

        return adjusted;
    }

    /// <summary>
    ///     Takes back every add the engine did not consume. A countdown still holding exactly what
    ///     was written was not stepped on this pass, so the group was skipped for a reason of its own
    ///     and the hold has nothing to correct; one that is exactly one lower was stepped, and the
    ///     pass netted to zero as intended. Anything else was rewritten during the loop, by a restart
    ///     or a teardown or one of the id-addressed calls, and is left alone.
    /// </summary>
    private void settle_restart_hold()
    {
        byte* managers = FhUtil.ptr_at<byte>(EngineAddresses.PpvFpManagers);

        for (int i = 0; i < FieldManagerCapacity; i++)
        {
            int written = _fp_restart_written[i];
            if (written == FieldRestartNotAdjusted) continue;

            int* countdown = (int*)(managers + i * FieldManagerStride);
            if (written == int.MaxValue)
            {
                if (*countdown == int.MaxValue - 1) *countdown = int.MaxValue;
                continue;
            }

            if (*countdown == written)
            {
                *countdown = written - 1;
                _fp_restart_undone++;
            }
            else if (*countdown == written - 1)
            {
                _fp_restart_held++;
            }
        }
    }
}
