namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Observe-only survey of the per-actor motion advance.
///
///     FUN_00838d10 is where an actor's motion speed enters its frame accumulator, and it is the
///     only reader of sg_rate in the whole image. Three paths leave it:
///
///     <list type="bullet">
///       <item>flag 0x40 clear: the speed is scaled by sg_rate, but only when the actor also
///             carries 0x100000 and KEEP_FPS is set;</item>
///       <item>flag 0x40 set and the sign bit set: a separate accumulator at actor+0x740 that
///             wraps against a per-motion length, with no rate correction anywhere;</item>
///       <item>flag 0x40 set and the sign bit clear: the same speed computation as the first path,
///             again with no rate correction.</item>
///     </list>
///
///     So two of the three paths cannot be corrected by KEEP_FPS at all. The first is gated on a bit
///     Ch_Allocate sets on every actor and only ChEvent.setKeepFps clears, so an actor that reaches
///     the advance without it was opted out by a cutscene script rather than left out by its data.
///     The global half of the gate is the one that moves in practice: it is clear for whole scenes
///     at a time, and while it is, no actor is corrected at all. This counts which path each actor
///     actually takes, which is the measurement that was missing.
///
///     The counts alone say how many actors run uncorrected but not which, so each path also names
///     the actors that reach it, once per distinct identity rather than once per frame.
///
///     Naming them is still not enough to say what the uncorrected advance costs, because the
///     damage is a clip reaching the end of its portion early and stopping there, and a name says
///     nothing about where a clip is. So the uncorrected actors also get their motion record
///     sampled over time: clip position, clip end, loops left, running flag, speed and hokan
///     divisor, on a slow heartbeat plus every change of the running flag.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const uint MotionFlagAltPath = 0x40;
    private const uint MotionFlagRated   = 0x100000;

    /// <summary>
    ///     The advance's own identity field: a ushort at offset 0. The parameter is declared
    ///     <c>ushort *</c> and the function's tail gates on <c>0x403c &lt; *param_1 &lt; 0x4042</c>,
    ///     so offset 0 holds a small tagged id that the engine itself compares against constants.
    ///
    ///     It is NOT the battle <c>Chr</c>. Reading Chr's model id at +0x4 and Chr.id at +0xc
    ///     produced a fresh pair on nearly every call - 475 distinct identities in thirty seconds -
    ///     which is what a struct of a different shape looks like when it is read as a Chr. The
    ///     flag word at +0x194 is common to both and is what made the mistake plausible.
    /// </summary>
    private const int MotionTagOffset = 0x0;

    /// <summary>
    ///     A scene can run more actors than are worth naming, and the log is not a per-frame trace.
    ///     Past this many distinct identities per path the survey keeps counting and stops naming.
    /// </summary>
    private const int MotionIdentityCap = 32;

    /// <summary>
    ///     The motion record fields a plain-path sample reports, all relative to the actor.
    ///
    ///     +0x4 is the model name, a char pointer the engine itself compares against "c001" and
    ///     passes to strstr. The clip time and its end are 8.8 fixed point animation frames, and the
    ///     end is stored as one less than the last frame. The speed multiplier is 8.8 and persists
    ///     across motion changes; the hokan divisor is the convergence filter's countdown, floored
    ///     at 1, where 1 means take the target outright.
    /// </summary>
    private const int MotionModelNameOffset = 0x4;
    private const int MotionClipTimeOffset  = 0x740;
    private const int MotionClipEndOffset   = 0x71c;
    private const int MotionLoopsOffset     = 0x724;
    private const int MotionRunningOffset   = 0x728;
    private const int MotionSpeedOffset     = 0x750;
    private const int MotionHokanOffset     = 0x752;

    /// <summary>
    ///     How many distinct actors the state sample will track. The actor array is a fixed table of
    ///     fixed-stride slots, so a pointer is a stable identity for as long as the scene runs and
    ///     the map cannot grow without bound; the cap is there so a scene with an unusual actor
    ///     count cannot turn the log into a per-frame trace either.
    /// </summary>
    private const int MotionSampleActorCap = 64;

    /// <summary>
    ///     Seconds between two state lines for the same actor. The value being watched moves every
    ///     frame, so a per-frame line would be unreadable and a line every few seconds would miss
    ///     the transition entirely; the compromise is a slow heartbeat plus an immediate line
    ///     whenever the motion-running flag changes, which is the event that decides the question.
    /// </summary>
    private const double MotionSampleIntervalSeconds = 1.0;

    private long _motion_calls;
    private long _motion_rated;      // 0x40 clear and 0x100000 set: the corrected path
    private long _motion_plain;      // 0x40 clear, no 0x100000: uncorrected
    private long _motion_alt;        // 0x40 set: the other accumulator, never corrected
    private uint _motion_flags_seen; // every bit ever observed, to see what an actor carries

    private readonly HashSet<ulong> _motion_identities = [];
    private readonly Dictionary<string, int> _motion_named_per_path = [];

    private long _motion_lent;
    private long _motion_held;

    private readonly Dictionary<nint, (double At, short Running)> _motion_samples = [];

    private string motion_counts()
        => $"mot_calls={_motion_calls} mot_rated={_motion_rated} mot_plain={_motion_plain} " +
           $"mot_alt={_motion_alt} mot_lent={_motion_lent} mot_held={_motion_held} " +
           $"mot_bits=0x{_motion_flags_seen:X} mot_named={_motion_identities.Count}";

    /// <summary>
    ///     Reports an actor the first time a given (path, tag, flags) combination is seen. Keying on
    ///     the flag word as well as the tag is deliberate: the question is which actors reach the
    ///     advance without 0x100000, and an actor whose classification changes mid-scene is the
    ///     interesting case rather than a duplicate. The header bytes go out with it, so the tag can
    ///     be checked against something rather than trusted.
    ///
    ///     Gated on the survey flag rather than on the hook being installed. The hook is installed
    ///     for the correction as well now, and naming every actor in a scene is a measurement, not
    ///     something a default run should be writing to the log.
    /// </summary>
    private void note_motion_actor(string path, nint actor, uint flags)
    {
        if (!_config.MotionSurvey) return;

        ushort tag = *(ushort*)(actor + MotionTagOffset);

        ulong key = ((ulong)path[0] << 56) | ((ulong)flags << 16) | tag;
        if (!_motion_identities.Add(key)) return;

        // Capped per path, not overall: the rated path carries nearly every actor and would
        // otherwise use up the budget before a single uncorrected one is named.
        _motion_named_per_path.TryGetValue(path, out int named);
        _motion_named_per_path[path] = named + 1;
        if (named >= MotionIdentityCap) return;

        uint head0 = *(uint*)actor;
        uint head4 = *(uint*)(actor + 4);
        uint head8 = *(uint*)(actor + 8);

        _logger.Info($"[Vrr] Motion {path}: tag=0x{tag:X4} flags=0x{flags:X8} " +
                     $"head=[{head0:X8} {head4:X8} {head8:X8}].");
    }

    /// <summary>
    ///     Reports where an uncorrected actor's clip actually is, repeatedly rather than once, so
    ///     the log carries the clip's position over time rather than the moment it was first seen.
    ///
    ///     What to look for: an actor whose motion-running flag has gone to 0 while its clip time
    ///     equals its clip end truncated to a whole frame - printed as PARKED - has run its portion
    ///     to the end and stopped on the last pose. An uncorrected actor doing that partway through
    ///     a scene is the double-speed advance confirmed outright, because the script's wait then
    ///     runs on to its authored length and hard cuts away from a pose the actor has been holding.
    ///
    ///     Sampled for every actor the engine's gate excludes, not only for the ones a script opted
    ///     out by name. The gate's global half is the one that is normally clear, so a sample taken
    ///     on the per-actor bit alone reports nothing in most scenes.
    ///
    ///     Called after the advance rather than before it, so a clamp the advance performs on this
    ///     frame is reported on the frame it happens.
    /// </summary>
    private void note_motion_state(nint actor)
    {
        short running = *(short*)(actor + MotionRunningOffset);

        if (_motion_samples.TryGetValue(actor, out (double At, short Running) last))
        {
            if (running == last.Running && ElapsedSeconds - last.At < MotionSampleIntervalSeconds) return;
        }
        else if (_motion_samples.Count >= MotionSampleActorCap)
        {
            return;
        }

        _motion_samples[actor] = (ElapsedSeconds, running);

        int time = *(int*)(actor + MotionClipTimeOffset);
        int end  = *(int*)(actor + MotionClipEndOffset);

        // The clamp writes the end truncated to a whole frame, not the end itself, so the two are
        // only equal after the low byte is masked off.
        bool parked = running == 0 && time == (end & unchecked((int)0xffffff00));

        nint name = *(nint*)(actor + MotionModelNameOffset);

        _logger.Info($"[Vrr] Motion uncorrected {(name == 0 ? "?" : Marshal.PtrToStringAnsi(name))}: " +
                     $"flags=0x{*(uint*)(actor + EngineAddresses.ActorFlagsOffset):X8} " +
                     $"t=0x{time:X8} end=0x{end:X8} loops={*(short*)(actor + MotionLoopsOffset)} " +
                     $"run={running} speed=0x{*(ushort*)(actor + MotionSpeedOffset):X4} " +
                     $"hokan={*(short*)(actor + MotionHokanOffset)}{(parked ? " PARKED" : "")}.");
    }

    private bool init_motion_survey()
    {
        // The lend and the hold ride on this hook, so it has to install for any of the three
        // options. Gating it on the survey alone left the lend silently inert on any config that
        // did not also ask to count.
        if (!_config.MotionSurvey && !_config.MotionAdvanceLendFlag && !_config.MotionHoldOptedOut) return true;

        return hook_or_log(HookSiteId.MotionSurveyMotionAdvance, "motion advance", EngineAddresses.MotionAdvance,
            () => new FhCallHandle<d_motion_advance>(new FhCallLocation(EngineAddresses.MotionAdvance, 0))
                .hook(this, h_motion_advance));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_motion_advance(nint actor, int mode);

    /* Counts, and corrects the actors the engine's own rate correction does not reach.
     *
     * This is where the motion rate correction belongs, because this is where the engine takes the
     * same decision. The advance multiplies its step by sg_rate only when the actor carries 0x100000
     * and the global KEEP_FPS is asserted, and both halves of that are script-writable at any time.
     * A correction decided anywhere else has to guess which way they will read when the step is
     * finally computed, and Ch_SetMotionSpeed writes a field that never gets reset, so a wrong guess
     * there is permanent. Decided here it lasts one call.
     *
     * The lend is the correction: hand the actor the bit and, where a script has cleared it, the
     * global, let the engine compute the step with its own sg_rate, then put both back. Nothing is
     * computed by the module, which is the point - sg_rate is already right in every case the module
     * would otherwise have to special-case. It is the presented-rate correction in ordinary play,
     * the recorded PS2 frame time while a scene is paced from syncdata, and exactly 1.0 during a
     * catch-up pass, where each extra pass is meant to be a full 30 Hz step.
     *
     * Both writes are safe to make around this one call because it is serial. The advance has one
     * call site, inline in Ch_CalcMain's third loop over the actor table, on the calling thread; the
     * job threads that loop dispatches run the per-element appliers, not the advance. Sg_GetKeepFps
     * has four callers in the image and the advance is the only one that can run inside this window.
     *
     * Actors on the 0x40 path are left alone. That branch has no sg_rate term at all, so lending
     * changes nothing there, and it still runs at the presented rate.
     *
     * The hold is the alternative and the only option that leaves the actor's own fields alone. On a
     * held frame the call is not made, which reproduces the 30 Hz call sequence exactly rather than
     * scaling the step - what a scene that counts animation frames rather than time was authored
     * against. It costs a pose that updates at 30 Hz inside a 60 Hz scene, so it is off by default
     * and takes precedence over the lend when it is on; running both would correct twice.
     *
     * Skipping the call also skips the copy at the end of it, which pulls three joint rotations from
     * an actor's owner into a weapon model in the w061 to w065 range. That copy is a plain overwrite
     * with no accumulation and the next call that does happen rewrites all of it, so the cost is the
     * same one frame of staleness the rest of the held actor already carries. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_motion_advance(nint actor, int mode)
    {
        uint* flags_ptr = actor == 0 ? null : (uint*)(actor + EngineAddresses.ActorFlagsOffset);
        bool  lent_flag   = false;
        bool  lent_global = false;
        bool  held        = false;
        bool  sampled     = false;
        sbyte keep_fps    = 0;

        if (flags_ptr != null)
        {
            uint flags = *flags_ptr;
            bool alt_path  = (flags & MotionFlagAltPath) != 0;
            bool opted_out = (flags & MotionFlagRated) == 0;

            keep_fps = KeepFps;

            // The engine's own gate, read the way the advance reads it. An actor that fails it takes
            // one whole animation frame per call and so runs at the presented rate.
            bool rate_corrected = !alt_path && !opted_out && keep_fps != 0;

            _motion_calls++;
            _motion_flags_seen |= flags;

            if (alt_path)        { _motion_alt++;   note_motion_actor("alt",   actor, flags); }
            else if (!opted_out) { _motion_rated++; note_motion_actor("rated", actor, flags); }
            else                 { _motion_plain++; note_motion_actor("plain", actor, flags); }

            if (!rate_corrected && !alt_path)
            {
                sampled = _config.MotionSurvey;

                if (_config.MotionHoldOptedOut && !advance_this_frame())
                {
                    held = true;
                    _motion_held++;
                }
                else if (_config.MotionAdvanceLendFlag && !_config.MotionHoldOptedOut)
                {
                    if (opted_out)     { *flags_ptr = flags | MotionFlagRated;              lent_flag   = true; }
                    if (keep_fps == 0) { FhUtil.set_at(EngineAddresses.SgKeepFps, (sbyte)1); lent_global = true; }
                    _motion_lent++;
                }
            }
        }

        try
        {
            if (!held)
            {
                new FhCallHandle<d_motion_advance>(new FhCallLocation(EngineAddresses.MotionAdvance, 0))
                    .chain_from(h_motion_advance).fnptr?.Invoke(actor, mode);
            }
        }
        finally
        {
            // Restored even if the call throws. A bit left set outside this window changes how every
            // other reader of the flag word sees the actor, and a global left set overrules the
            // scene for every subsystem that reads it, not only this one. The global is put back to
            // the value it held rather than to 1, because scripts push 2 as well as 0 and 1.
            if (lent_flag)   *flags_ptr &= ~MotionFlagRated;
            if (lent_global) FhUtil.set_at(EngineAddresses.SgKeepFps, keep_fps);
        }

        if (sampled) note_motion_state(actor);
    }
}
