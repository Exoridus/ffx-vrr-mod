namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Per-correction toggles. Every retiming hook is individually switchable so a
///     regression can be bisected without rebuilding, and so a subsystem that is not
///     yet correct at 60 Hz can be disabled and reported rather than silently
///     falling back to 30 Hz.
/// </summary>
public sealed record VrrConfig
{
    /// <summary>Force the present path off the engine's 30 Hz vsync interval.</summary>
    public bool Present { get; init; } = true;

    /// <summary>
    ///     What to write into the engine's frame limiter. 1 is 59.94 Hz, 2 is half of that, and
    ///     0 removes the limit: the WinMain pump multiplies this value by one frame at 59.94 to get
    ///     the time it must sleep, so zero means it never sleeps and the game presents as fast as it
    ///     can.
    ///
    ///     Higher rates have incomplete native coverage. Particle timelines require supported exact
    ///     divisors, and live transitions can be refused while objects retain a finer phase.
    /// </summary>
    public uint PresentInterval { get; init; } = 1;

    /// <summary>Optional validated 140-frame menu-water pack installed in a mod efl/x directory before startup.</summary>
    public string? ExpandedMenuWaterPack { get; init; }

    /// <summary>
    ///     Legacy override for the simulation rate selected at startup. Null uses the F7 setting,
    ///     which defaults to 60 Hz. Changing the selected simulation rate requires a restart;
    ///     render measurements never alter the active rate.
    /// </summary>
    public double? TargetFramerateOverride { get; init; }

    /// <summary>Suppress engine code that resets the flip vsync interval back to 30 Hz.</summary>
    public bool KeepVsyncInterval { get; init; } = true;

    /// <summary>
    ///     Rescale the two vertical blank counters to the selected simulation rate. This is the
    ///     correction every other one in this module compensates for: the engine advances both
    ///     counters by a hardcoded 2 per frame and Sg_MainCalcRate turns their delta into the
    ///     animation rate, so with them left alone at 60 Hz the rate stays at 1.0 and every
    ///     animation takes a full 30 Hz step twice as often.
    ///
    ///     It is on by default and there is no reason to turn it off in play. The switch exists
    ///     because it is the baseline the rest can be told apart against: with this off at 60 Hz,
    ///     anything still mistimed is mistimed for a reason of its own rather than through the rate.
    ///     Turning it off also stops the frame-skip sampling, which is taken inside the same hook.
    /// </summary>
    public bool VBlankRate { get; init; } = true;

    /// <summary>Feed the character update a delta derived from the selected simulation rate.</summary>
    public bool CharacterDelta { get; init; } = true;

    /// <summary>
    ///     Scale ATEL frame waits to the active refresh rate. Covers both halves of call target 0000:
    ///     the frame count the init handler stores, and the movie-frame delta the exec handler
    ///     subtracts from it while a movie with its own camera is playing.
    /// </summary>
    public bool AtelWaits { get; init; } = true;

    /// <summary>Retime camera move durations and accelerations.</summary>
    public bool Camera { get; init; } = true;

    /// <summary>
    ///     Enable VRR/tearing presentation (swapchain ALLOW_TEARING, uncapped present interval) and
    ///     the camera/character render interpolation that goes with it. Defaults to false until the
    ///     swapchain hook (Task 4) is in place and proven; flipping this default is a one-line change
    ///     once that manual verification passes.
    /// </summary>
    public bool VrrEnabled { get; init; } = false;

    /// <summary>Retime fade, flash and alpha ramps.</summary>
    public bool Fades { get; init; } = true;

    /// <summary>
    ///     Correct the camera cross-fade by holding filter slot 0 instead of scaling the frame count
    ///     Sg_AccSetAlpha receives.
    ///
    ///     Slot 0 is the one filter slot that does not recompute its alpha from a counter and a
    ///     total. It walks by a step fixed once at max(1, |alpha - current| / frames), and that step
    ///     is an integer with a floor of 1, so once frames exceeds the alpha delta a doubled frame
    ///     count buys nothing and the fade runs at twice the speed it should. Across the 425 ramping
    ///     call sites in the script corpus the delta is always 128, and 61 of them come out wrong in
    ///     one direction or the other: frames=120 finishes twice too fast, frames=35 takes half again
    ///     as long as it should.
    ///
    ///     Holding the slot emits the engine's own integer sequence at half rate, which is exact in
    ///     both directions. The two halves are one switch because they only work together: passing
    ///     the frame count through without the hold leaves every cross-fade twice too fast.
    /// </summary>
    public bool CrossFadeHold { get; init; } = true;

    /// <summary>
    ///     Retime motion and effect speeds.
    ///
    ///     Ch_SetMotionSpeed is hooked but passes its argument through. It writes actor+0x750, which
    ///     nothing resets, so a rate correction applied there outlives the flag state that justified
    ///     it and the engine then corrects the same actor a second time for the rest of the scene.
    ///     The motion rate correction lives at the advance instead, under
    ///     <see cref="MotionAdvanceLendFlag"/>; the hook stays for its call counter and because the
    ///     reasoning belongs next to the address.
    /// </summary>
    public bool Motion { get; init; } = true;

    /// <summary>
    ///     Correct the motion advance for every actor the engine's own gate excludes, by lending it
    ///     that gate for the duration of one call.
    ///
    ///     The advance multiplies its accumulator step by sg_rate only when the actor carries flag
    ///     0x100000 and the global KEEP_FPS is asserted. An actor that fails either half takes one
    ///     whole animation frame per call, which is double wall speed at 60 Hz: a portion played
    ///     with loops=1 reaches its end in half the authored time, the advance clamps the clip to
    ///     the last whole frame and clears the running flag, and the actor parks on that pose while
    ///     the script's wait runs on to its correct length and cuts elsewhere. What that looks like
    ///     is a limb dropping in a single frame.
    ///
    ///     Both halves fail in practice, and the global one is the common case rather than the rare
    ///     one. Ch_Allocate sets the actor bit on every actor and only ChEvent.setKeepFps clears it,
    ///     which is why deployed telemetry has never once seen an actor reach the advance without
    ///     it; the global is cleared by scripts through sgSetKeepFps and stays clear for whole
    ///     scenes, and telemetry reports it clear for roughly half the sampled play time.
    ///
    ///     The module computes nothing here. The actor is handed the bit, the global is handed the
    ///     value the engine tests for, the engine does its own arithmetic, and both are put back.
    ///     That matters because sg_rate is already right in situations the module would otherwise
    ///     have to special-case: it is the presented-rate correction in ordinary play, the recorded
    ///     PS2 frame time while a scene is paced from syncdata, and exactly 1.0 during a catch-up
    ///     pass, where an extra simulation pass is meant to be a full 30 Hz step rather than a
    ///     scaled one.
    ///
    ///     Actors on the 0x40 path are left alone: that branch has no sg_rate term at all, so
    ///     lending changes nothing there.
    /// </summary>
    public bool MotionAdvanceLendFlag { get; init; } = true;

    /// <summary>
    ///     Correct the same actors by skipping the motion advance on frames the module holds,
    ///     instead of by lending the gate. The alternative to <see cref="MotionAdvanceLendFlag"/>,
    ///     not an addition to it: where both are on, the hold takes the actor and the lend leaves it
    ///     alone, because scaling a step that is only taken half as often corrects it twice.
    ///
    ///     What the hold buys is exactness for a scene that counts animation frames rather than
    ///     time. Lending scales the step, so a clip lands on positions the authored 30 Hz sequence
    ///     never visited; holding reproduces that sequence call for call and writes neither the
    ///     speed multiplier nor the step.
    ///
    ///     What it costs is why it is off. The actor's drawn pose updates at 30 Hz while everything
    ///     around it updates at 60, which is visible if it moves fast; and the stationary predicate
    ///     that selects the cheap per-element applier is fed from optpos, which is calculated
    ///     elsewhere and still runs every frame, so a held actor reads as not moving and becomes
    ///     eligible for a filtered path worth up to 0.70 degrees of lag per joint.
    /// </summary>
    public bool MotionHoldOptedOut { get; init; }

    /// <summary>Retime the battle limit timer.</summary>
    public bool BattleTimers { get; init; } = true;

    /// <summary>
    ///     Treat the engine as already correctly paced while it is running from the syncdata table.
    ///
    ///     Sg_MainCalcRate takes sg_rate from g_sgSyncRate whenever g_isNeedSync is set, and the
    ///     catch-up loop then holds the simulation to the PS2 frame times the table records. Every
    ///     duration this module scales is wrong in that state, because the scene is already playing
    ///     at its authored rate. Roughly a third of the recorded frames ask for a rate other than
    ///     nominal, across three cutscenes.
    /// </summary>
    public bool SyncDataAware { get; init; } = true;

    /// <summary>
    ///     Refuse the recorded pacing outright, so the three syncdata scenes run like any other and
    ///     this module's corrections apply to them.
    ///
    ///     <para>
    ///     <see cref="SyncDataAware"/> is the polite answer: it stands aside and lets the engine play
    ///     the scene as recorded. What that costs is measurable - run 20260912_100226 in azit0300
    ///     presented at 20.0, 20.2, 21.2, 16.2 and 15.3 fps across consecutive windows, because a
    ///     third of the recorded frames ask for 1.5x nominal and FUN_00821F90 busy-waits out the
    ///     difference. Coming from 60 Hz everywhere else, that reads as the game breaking.
    ///     </para>
    ///
    ///     <para>
    ///     The lever is iSyncGetData's return value. 999 is the engine's own answer for a scene with
    ///     no recording, and FUN_00821E80 responds to it by clearing g_isNeedSync, both PS2 time
    ///     accumulators, the rate index and the rate table - so this takes a path the engine already
    ///     has rather than writing a state it would not otherwise be in. The speedrun mod reaches the
    ///     same end by zeroing a byte in the sync manager at three hand-picked sites; this needs no
    ///     per-scene knowledge.
    ///     </para>
    ///
    ///     <para>
    ///     <b>It breaks the dialogue, and that is measured rather than feared.</b> Played in azit0300
    ///     on 2026-09-12 the scene rendered cleanly at the display rate and its voice lines were cut
    ///     off by the next one - Rikku's stopped when Wakka's began - with subtitles advancing early
    ///     by the same margin. The recording is about 25 percent longer than nominal (8,469 frames
    ///     averaging field_count 2.51 against 2), the script counts frames, and the voice tracks are
    ///     cut against the dilated length. So this is a scrubbing tool: right for getting past a
    ///     scene, wrong for watching one.
    ///     </para>
    ///
    ///     <para>
    ///     The speedrun mod is not a precedent for watching either, which is worth writing down
    ///     because its comment reads like one. It zeroes the same timing at three sites and at every
    ///     one the next statement warps past the scene - the Chimeras encounter, a battle, a jumped
    ///     cutscene value. What it removes is the residual slowdown around a skip.
    ///     </para>
    ///
    ///     <para>
    ///     Retiming the scene instead of disabling it is not a scale factor, which is why it is not
    ///     offered here: g_sgSyncRate is indexed by recorded frames consumed rather than by presented
    ///     frames, so halving sg_rate would consume the recording twice as fast instead of stretching
    ///     it. Preserving both the length and the placement of the dilation means advancing the
    ///     recording at half speed and splitting each entry across the presented frames.
    ///     </para>
    /// </summary>
    public bool SyncDataDisable { get; init; }

    /// <summary>
    ///     Attach the two counters inside the motion path that sg_rate never reaches - the
    ///     cross-fade length and the sequence VM's wait - and report how often they run.
    /// </summary>
    public bool MotionSequenceCounters { get; init; } = true;

    /// <summary>
    ///     Hold those two counters on skipped frames, so a cross-fade and a scripted wait take the
    ///     same wall clock time they were authored for. Separate from the counters so the hooks can
    ///     be measured before they change anything.
    /// </summary>
    public bool MotionSequenceHold { get; init; } = true;

    /// <summary>
    ///     Scale the 90-frame hold the battle entry waits out before the encounter starts. It counts
    ///     presented frames, so at 60 Hz the transition blur runs 1.5 seconds instead of 3 and the
    ///     opening camera arrives early - the part of "the intro is too fast" that no camera hook
    ///     reaches.
    /// </summary>
    public bool BattleIntroBlur { get; init; } = true;

    /// <summary>
    ///     Hold the texture video update. This is the second video path, separate from the FMV
    ///     player, and it advances once per presented frame with no rate of its own.
    /// </summary>
    public bool TextureVideo { get; init; } = true;

    /// <summary>
    ///     Hold the main menu water animation so its 69 prepared images play at their authored rate.
    ///     Frame skipping, and therefore 30 Hz inside a 60 Hz game.
    /// </summary>
    public bool MenuWater { get; init; } = true;

    /// <summary>
    ///     Hold the FMV texture update so 29.97 video plays at its authored rate. Frame skipping.
    ///     Turn this off once the shipped videos are encoded at the target framerate.
    /// </summary>
    public bool Fmv { get; init; } = true;

    /// <summary>
    ///     Hold the old-format character texture animation advance. The two texture animation
    ///     formats are retimed differently: the new one follows Ch_TextureSetAnimTimer, which the
    ///     motion retiming already scales, while the old one counts in literal increments and can
    ///     only be held. 445 of 872 character model directories are on the old path.
    /// </summary>
    public bool TextureAnimation { get; init; } = true;

    /// <summary>
    ///     Halve the particle system's own clock: the age step in the dispatcher and, with it, the
    ///     time step every manager is started with.
    ///
    ///     This is the timeline rather than the motion - when a keyframe fires, when a step loops,
    ///     when an object dies, when the next one is emitted. It is a rate change in the image, and
    ///     it cannot miss a keyframe: a tag fires on equality, so every tag that works today is a
    ///     multiple of the old step and the halved sequence is a strict superset of the old one.
    ///
    ///     The two halves belong together. Halving only the age would double the standing population
    ///     - the same emission rate per frame against twice as many frames of life - and halving only
    ///     the manager step is what earlier attempts did, which changed nothing visible because the
    ///     motion is not in it.
    /// </summary>
    public bool ParticleTimeline { get; init; } = true;

    /// <summary>
    ///     Hold whole effect-advance calls on intermediate frames. Some advances also prepare
    ///     particle geometry, so this can flicker and double-retime separately corrected particles.
    ///     Disabling it preserves that preparation but leaves VM script waits uncorrected.
    /// </summary>
    public bool EffectHold { get; init; } = true;

    /// <summary>
    ///     Hold the effect advance in battle too, rather than only where the call is inert.
    ///
    ///     The battle exemption existed because the hook itself used to kill the game before the
    ///     first splash, which was read as the hold being unsurvivable. It was the signature: the
    ///     generated binding declares MsEffectProcess without parameters while the function reads
    ///     its mode from the stack, so the detour handed it garbage. With a cdecl one-argument
    ///     delegate the hook is ordinary, and the hold is what it was meant to be.
    ///
    ///     What to expect: of 581 overlays, 224 have a stub advance (33 C0 C3) and the hold is a
    ///     precise no-op for them - those effects are driven by character motion instead. Of the
    ///     357 with a real advance, about 117 read Sg_GetCurExecFrames inside their draw, so their
    ///     timeline is partly on the other side and a held advance leaves them internally
    ///     inconsistent rather than merely slower. Look for effects that look wrong, not for
    ///     effects that are still fast.
    /// </summary>
    public bool EffectHoldInBattle { get; init; } = true;

    /// <summary>
    ///     Hold the eternal effect set's second object list along with the first.
    ///
    ///     Its two entry points are not an advance and a draw: both run the same worker over a
    ///     different object list, and both decrement the per-channel wait bytes that are the set's
    ///     only clock. MsEffectProcess reaches the first from mode 0 and the second from mode 1, and
    ///     only mode 0 is held, so with this off the set runs half retimed and half not - which is
    ///     the defect this module has already shipped three times, not a safe default.
    ///
    ///     <para>On since 2026-09-12, and the measurement is what changed.</para> It was off because
    ///     the opcode handlers behind that worker were unread and "a hold is safe" was probable
    ///     rather than established. All 119 of them have now been read the only way that settles it:
    ///     not one reads sg_count, sg_rate, sg_ratef, the presented-frame counter, the frameskip
    ///     counter, g_isNeedSync or Sg_GetCurExecFrames, and neither does any of the 364 functions
    ///     they call. There is no clock inside the VM that would keep running while it is held. The
    ///     one risk that was named, op_spr_alloc and op_spr_free around the list, appears only one
    ///     level down and always as a pair in the same function, so it is scratch rather than a pool
    ///     something else drains per frame.
    /// </summary>
    public bool EternalEffectHold { get; init; } = true;

    /// <summary>
    ///     Attach the MsEffectProcess hook and count advance and draw calls without holding
    ///     anything. Separates the two failure modes: a hook that the game cannot survive at all,
    ///     and a hold the overlay's state machine cannot survive.
    /// </summary>
    public bool EffectProbe { get; init; }

    /// <summary>
    ///     Hold the field particle group restart countdown on skipped frames, so a group's absence
    ///     and its restart land at the wall time they were authored for.
    ///
    ///     pppFpLoop decrements a counter at each manager's +0x00 once per pass with no time term,
    ///     then tests the result. While the result is still zero or above the group is skipped
    ///     outright, drawing nothing; at -1 every object is freed and the group is started again in
    ///     the same pass as the decrement. At 60 Hz that absence lasts half as long as it should and
    ///     the restart arrives twice as early, which is field particles vanishing and popping back in.
    ///
    ///     It is not a hold on the call. The draw packet is built inside the same pass as the
    ///     advance, so skipping any part of pppFpLoop would drop a group's contribution to the frame;
    ///     what is held is the counter, by adding one back before the engine's own decrement and
    ///     taking it off again for any group the engine turned out not to step.
    /// </summary>
    public bool FieldParticleRestartHold { get; init; } = true;

    /// <summary>
    ///     Hold the particle step kernels that move an object, leaving the ones that draw it alone.
    ///
    ///     This is the retiming the particle system's own structure allows: a program step carries a
    ///     separate update and draw entry point, and only the update advances anything. Holding the
    ///     update on skipped frames halves the motion while every frame still draws.
    /// </summary>
    public bool ParticleKernelHold { get; init; } = true;

    /// <summary>
    ///     Force the event worker's animation curves onto the vertical blank clock.
    ///
    ///     yiAnimInfo_init copies KEEP_FPS into the mode global once, and the worker then reads
    ///     either yiGetVCount, which this module rescales, or yiGetFCount, which is sg_count * 2.
    ///
    ///     This is not a choice between two equivalent clocks. sg_count is incremented once per
    ///     Sg_MainLoop pass, so it runs at the presented rate, and it cannot be rescaled: six
    ///     consumers select a GS double buffer off its parity. The yiGetFCount branch is therefore
    ///     twice too fast at 60 Hz with no way to correct it in place, while the vblank branch is
    ///     delta-measured and correct. The worker divides a difference of two samples by a duration
    ///     held in the animation record, both in counter units, so curve progress is directly
    ///     proportional to the counter rate and nothing downstream absorbs the factor.
    ///
    ///     Forcing the mode is safe by construction: yiAnimInfo_init zeroes the start and now
    ///     stamps of all 64 tracks before this module writes the mode, so no track can end up with
    ///     its two timestamps taken from different clocks.
    ///
    ///     One documented behaviour change: below the target rate, forced vblank curves keep
    ///     running on wall clock instead of slowing with the rendered frames. That is what every
    ///     battle and the ~20 scenes calling sgSetKeepFps(1|2) already do.
    /// </summary>
    public bool EventClockOnVblank { get; init; } = true;

    /// <summary>
    ///     Count how the per-actor motion advance classifies the actors a scene runs, without
    ///     changing anything.
    ///
    ///     Its rate correction is gated on the actor's flag 0x100000 and on the global KEEP_FPS.
    ///     Ch_Allocate sets the actor flag on every actor and only ChEvent.setKeepFps clears it, so
    ///     the counters separate the actors a cutscene script opted out from the rest, and name
    ///     them. An earlier reading held that nothing in the executable set the bit; the write is an
    ///     XOR mask rather than an OR, which is why an enumeration of OR and TEST operations missed
    ///     it.
    /// </summary>
    public bool MotionSurvey { get; init; }

    /// <summary>
    ///     Hold only these step kernels, by name. Empty means all of them.
    ///
    ///     Not every update is only motion. A kernel that initialises what it just spawned, or that
    ///     maintains geometry a draw kernel then reads, misbehaves when it is skipped rather than
    ///     merely running at half rate - which is why the set is narrowed from a config file
    ///     instead of a rebuild.
    /// </summary>
    public string[] ParticleKernelOnly { get; init; } = [];

    /// <summary>Leave these step kernels running at full rate, by name.</summary>
    public string[] ParticleKernelExcept { get; init; } = [];

    /// <summary>
    ///     Retime the eight generic integrators by keeping half of what each call adds, instead of
    ///     skipping every second call. Takes precedence over the hold for those eight; every other
    ///     step kernel is unaffected and still held.
    ///
    ///     The hold gets the average rate right and the image wrong: a held kernel leaves its object
    ///     untouched for a whole frame, so particles advance on 30 of 60 frames while the camera
    ///     advances on all of them, and an object can die or be born between two updates - which is
    ///     a particle vanishing instead of fading out, or appearing at full opacity instead of
    ///     fading in. Scaling the delta gives the same wall-clock rate with a valid value on every
    ///     frame.
    ///
    ///     Applies to the four kernels that integrate floats - pppMove, pppAccele, pppSclMove and
    ///     pppSclAccele, so position and scale. Those are exact at any rate. The four that integrate
    ///     integers need <see cref="ParticleIntegratorScaleIntegers"/> as well, because they are not.
    ///
    ///     The kernels are hooked in FFX.exe by RVA and a magic overlay's own program table holds
    ///     thunks into them, so this reaches battle and magic effects as well as field particles.
    ///
    ///     <para>
    ///     <b>Do not enable this without reading what follows.</b> It has now failed in play twice,
    ///     and the second failure removes the explanation the first one was given.
    ///     </para>
    ///
    ///     <para>
    ///     The first failure was blamed on the integer integrators - 2.18 M of 2.46 M scaled calls
    ///     carried a remainder - so the mode was narrowed to the four float kernels, where the
    ///     arithmetic is exact and int_carried measured 0 over 644,650 calls. It failed anyway, and
    ///     worse. Whole effects stopped appearing: the save sphere's orb, a campfire, the teleport
    ///     effect at the end of the Gagazet cave. The teleport is the one that matters, because it
    ///     is not a rendering symptom - the event script never continued, so the player vanished
    ///     and was never moved. FFX scripts wait on effects, so an effect that never starts or
    ///     never reports itself finished stalls the scene. Toggling this flag off and restarting
    ///     restored all four, with nothing else changed.
    ///     </para>
    ///
    ///     <para>
    ///     So the defect is not arithmetic precision. Something about writing a scaled value back
    ///     into the integrated slot breaks the program flow that reads it - most likely because
    ///     these slots are shared: a keyframe kernel writes the rate slot that a Move kernel
    ///     integrates, and other steps read the integrated slot in the same pass. Halving what one
    ///     kernel contributed is not the same as halving the timestep, and the difference reaches
    ///     whatever else consumes that slot.
    ///     </para>
    ///
    ///     <para>
    ///     Reviving it needs a narrower experiment than this flag offers: one kernel at a time,
    ///     against a scene whose effects are known to be visible, watching for an effect that fails
    ///     to appear rather than for a rate. Until then the hold is the correct behaviour and
    ///     particle position and scale stay at 30 Hz inside a 60 Hz image.
    ///     </para>
    /// </summary>
    public bool ParticleIntegratorScale { get; init; }

    /// <summary>
    ///     Extend <see cref="ParticleIntegratorScale"/> to the four integer integrators: pppAngMove
    ///     and pppAngAccele on 32-bit binary angles, pppColMove and pppColAccele on 16-bit colour.
    ///     Without this they stay on the hold, which is the default.
    ///
    ///     They are separate because the arithmetic is not exact for them and cannot be made so
    ///     from outside. A delta of 5 halved is 2.5, which the field cannot hold, so the remainder
    ///     is alternated by frame instead - two consecutive frames then add what one vanilla frame
    ///     added. That is exact at Scale 2, where the only possible remainder is one half, and only
    ///     there: at Scale 4 a delta of 6 wants 1.5 per call and the alternation delivers 1.25,
    ///     because a single per-frame flag can carry one remainder and these need three. Carrying
    ///     them properly needs a residual per object and per slot, and a particle object is
    ///     recycled heap with nowhere to keep one.
    ///
    ///     Colour is also where the visible risk is: it drives alpha, so a rounding that lands
    ///     wrong is a particle that is too bright or invisible rather than one that is slightly
    ///     mispositioned.
    /// </summary>
    public bool ParticleIntegratorScaleIntegers { get; init; }

    /// <summary>
    ///     Overwrite the hardcoded 29.97 the video update loop multiplies its time step by
    ///     (a double in .rdata at 0x74A180). Null leaves it alone.
    ///
    ///     Leave this alone. It is not the playback clock and setting it does active harm.
    ///
    ///     The player's milliseconds per picture come from the container through a different
    ///     constant entirely; this one has exactly three readers and all of them convert a
    ///     millisecond position into the progress index that ATEL sees. Scripts compare that index
    ///     against hard thresholds - the intro waits for it to reach 4720 - so doubling this value
    ///     halves every such threshold and cuts each video off halfway through.
    /// </summary>
    public double? VideoTargetFramerate { get; init; }

    /// <summary>
    ///     Count calls to the texture animation candidates and report them in the telemetry line.
    ///     Changes no behaviour; it identifies which function is on the live path.
    /// </summary>
    public bool SurveyTextureAnimation { get; init; } = true;

    /// <summary>
    ///     Move the battle command window's multi-target cursor blink to a higher bit of sg_count, so
    ///     it blinks at the authored 15 Hz instead of at half the presented rate.
    ///
    ///     sg_count itself cannot be rescaled - six of its readers pick a GS double buffer off its
    ///     parity - so the correction is a one-byte change to the mask in the one instruction that
    ///     reads it as a blink. A higher bit of a counter is still a fifty percent square wave, so
    ///     the on-time is preserved along with the period.
    /// </summary>
    public bool BattleCursorBlink { get; init; } = true;

    /// <summary>
    ///     Raise the star pass threshold of the dream and recall overlay, so its star field is
    ///     redrawn at the authored 10 Hz instead of at a third of the presented rate.
    ///
    ///     The stars are drawn on one call in three and not at all on the other two, so raising the
    ///     threshold restores the cadence but shortens the on-time in proportion: at 60 Hz the pass
    ///     lands every sixth frame and lasts one frame instead of every third and lasting one. The
    ///     alternative, holding graphicDrawDream itself, would alternate the full star field with the
    ///     single cached quad its replay path draws, which is a visible flicker.
    /// </summary>
    public bool DreamOverlayStars { get; init; } = true;

    /// <summary>
    ///     Scale the sprite frame clock of the lens and flare family, which advances a fixed step per
    ///     drawn frame and is therefore the rhythm of a flare or a background flash. Unlike every
    ///     other particle correction this is a true scale: no sprite frame is dropped, because the
    ///     accumulator carries its remainder.
    /// </summary>
    public bool LensSpriteClock { get; init; } = true;

    /// <summary>Retime per-draw particle geometry UV integration without skipping rendering.</summary>
    public bool ParticleUvClock { get; init; } = true;

    /// <summary>Retime Shape and ShapeX sprite clocks while preserving native draws. Requires native acceptance testing.</summary>
    public bool ParticleSpriteClock { get; init; }

    /// <summary>
    ///     Retime new-format weapon and character texture animation within each native update.
    ///     Preserve native sprite/blink pauses and all material outputs; scale private scroll
    ///     increments independently of the discrete sprite clock.
    /// </summary>
    public bool TextureAnimationStep { get; init; } = true;

    /// <summary>
    ///     Log the first calls to MsCameraMoveAcc with all seven arguments. The battle entry camera
    ///     is an ATEL script that calls camMoveAcc(0, 0, 30, 15) in 540 of 641 encounter scripts, so
    ///     the logged arguments say directly whether the scaling reached it.
    /// </summary>
    public bool SurveyCamera { get; init; } = true;

    /// <summary>
    ///     Correct the Eternal Calm copyright card, which Sg_MainLoop advances once per pass: its
    ///     alpha ramp and the 0x130 frame counter that ends the sequence both run at the presented
    ///     rate, so at 60 Hz the card fades in twice too fast and disappears after 5 seconds rather
    ///     than 10.
    ///
    ///     The correction is a half-rate detour rather than a hold, because skipping the call would
    ///     leave the frame with no card drawn at all.
    /// </summary>
    public bool EternalCalmCard { get; init; } = true;

    /// <summary>
    ///     Correct the idle look-around every standing actor runs. Its dwell and its step are both
    ///     counted in calls and nothing in the engine scales them, so at 60 Hz a character turns its
    ///     head twice as fast and twice as often as it was authored to.
    ///
    ///     The correction suppresses the advance on skipped frames rather than skipping the call:
    ///     the call also rebuilds the rotation of the optpos element the following pass consumes,
    ///     and a frame without it is a frame with that rotation unwritten.
    /// </summary>
    public bool IdleSway { get; init; } = true;

    /// <summary>
    ///     Correct the head and neck tracking every actor runs while something has its attention.
    ///     Its weight ramp, its yaw blend and its slerp fade all step once per call and nothing in
    ///     the engine scales them, so at 60 Hz a character snaps its head onto a target in half the
    ///     authored time and the blend out of a changed target is over twice as fast.
    ///
    ///     The correction holds the three clocks on skipped frames rather than skipping the call:
    ///     the call is what writes the replace matrix and the weight of optpos element 9, and the
    ///     same worker pass consumes them a few instructions later.
    /// </summary>
    public bool NeckTracking { get; init; } = true;

    /// <summary>
    ///     Correct the vertical channel of a swimming actor: the buoyancy that lifts it towards the
    ///     surface, the gravity that pulls it back, the velocity clamp, and the smoothed foot offset
    ///     the ground clamp is measured against. Five rates, all a step per call, none of them
    ///     reading the delta Ch_CalcMain is handed - so at 60 Hz a swimmer bobs, sinks and settles
    ///     twice as fast as it was authored to.
    ///
    ///     The correction holds the three fields on skipped frames rather than converting the rates.
    ///     Two of the five are accelerations and one is a velocity, so a conversion would scale one
    ///     group by the framerate factor and the other by its square, and three of the five sit on
    ///     .rdata constants shared with the rest of the executable. The hold picks no constant.
    ///
    ///     Gravity mode 2 is reachable only from the ATEL opcode setGravityMode - 129 sites in 29
    ///     event scripts, none in any battle or monster script - so this only ever fires in field
    ///     swimming, and the telemetry counter reads zero for almost all of a playthrough.
    /// </summary>
    public bool Buoyancy { get; init; } = true;

    /// <summary>
    ///     Correct the ATEL worker's own motion and rotation records, which drive every event
    ///     character, the field camera and every attached map object.
    ///
    ///     Each ATEL worker carries nine move records and nine rotation records, one per thread
    ///     priority, and two readers step them once per presented frame. Everything in them is
    ///     counted in calls: a yaw and a pitch turn step in radians, four rotation rates, a float
    ///     interpolation accumulator that advances by one, and two watchdog deadlines the readers
    ///     compare their own per-call counter against. Nothing in the engine scales any of it, so at
    ///     60 Hz an event character turns twice as fast and a scripted rotation finishes in half the
    ///     authored time while the wait that follows it now runs twice as long.
    ///
    ///     The rates are divided at their setters, which are leaf functions with one caller each.
    ///     The deadlines are multiplied at theirs. The accumulator cannot be corrected at a setter -
    ///     startMotion and startRotation overwrite the duration with its reciprocal for half the
    ///     types, and move type 7 takes its step from the FMV decoder while a movie with its own
    ///     camera is playing - so it is corrected at the increment instead, by a pair around the two
    ///     readers that rewrites the accumulator by the delta the reader just produced.
    /// </summary>
    public bool AtelWorkerMotion { get; init; } = true;

    /// <summary>
    ///     Divide the script's per-call acceleration at move+0x34 by the scale. <b>Off by default,
    ///     and the reason is a measurement rather than caution.</b>
    ///
    ///     setGravity [0094h] writes the field and the move reader reads it in exactly two places:
    ///     as the increment in <c>v += a</c>, and as the brake ceiling in <c>sqrt(2 a d)</c>. Those
    ///     two want different exponents and no single factor satisfies both. The stored velocity
    ///     keeps its 30 Hz magnitude - the integrator divides it at the point of use - so the
    ///     increment needs <c>a/scale</c> to take twice as many calls to reach the same velocity,
    ///     while the brake ceiling is a velocity at a given distance and must stay at <c>a</c>
    ///     unchanged. Dividing satisfies the first and multiplies the second by <c>1/sqrt(scale)</c>,
    ///     which makes the final approach to a destination about 29 percent slow at 60 Hz.
    ///
    ///     Both errors are sub-frame against the shipped scripts, which is why this is off rather
    ///     than reshaped. 84 call sites in 18 event scripts, and the authored acceleration is at or
    ///     above the movement speed in nearly every pairing (g=20 against speeds of 10 to 17 in the
    ///     Luca scripts, g=70 against 34), so the increment saturates the speed cap within one call
    ///     and the braking phase lasts under one 30 Hz frame. Only 67 of 12,289 move starts use a
    ///     type that reads the field at all.
    ///
    ///     It reaches no thrown blitzball. The blitzball corpus starts 723 move type 2 and four type
    ///     1, and neither type reads this field; its five setGravity(10) calls arm a record nothing
    ///     then looks at, and the arc comes from the Ch gravity mode instead. See
    ///     finding:atel-move-accel-is-read-by-three-move-types.
    /// </summary>
    public bool AtelWorkerGravity { get; init; }

    /// <summary>
    ///     Divide the three per-call facing turn rates every character uses, so an actor turns into
    ///     its walk instead of snapping round and then setting off.
    ///
    ///     Ch_CalcMain's two loop-1 motion controllers step the facing at Chr+0x158 towards its
    ///     target by one of three constants chosen by the actor's speed - 18 degrees per call
    ///     standing, 20 walking, 30 running - and neither reads the delta the rest of loop 1 is
    ///     handed. At 60 Hz that is 1080, 1200 and 1800 degrees per second against an authored 540,
    ///     600 and 900, while the translation the same loop performs is delta-corrected and still
    ///     takes the authored time.
    ///
    ///     Applied as an operand rewrite at the six instruction sites rather than as a hook or as a
    ///     write to the literal pool: one of the three constants has a third reader elsewhere in the
    ///     image, and the step helper the constant is an argument to has a third caller in
    ///     Ch_CalcElement that is a different quantity. Divided rather than held - holding the facing
    ///     would freeze turning on every second frame while translation kept running, which is why
    ///     the buoyancy hold leaves the same field alone.
    /// </summary>
    public bool ChFacingTurnRate { get; init; } = true;

    /// <summary>
    ///     Stretch the frame count of every character fade, tint and transparency ramp, so a
    ///     blackout, dissolve or actor light change takes the time it was authored for.
    ///
    ///     Each actor carries seven <c>{current, target, frames}</c> records at Chr+0x330 and
    ///     Ch_CalcMain steps all seven once per presented frame with no time term:
    ///     <c>current += (target - current) / frames; frames--</c>. At 60 Hz every one of them
    ///     finishes in half its authored wall clock.
    ///
    ///     Corrected by multiplying the count at the single arm helper all seven setters funnel
    ///     into. That is exact rather than approximate, because the step divides by the count that
    ///     is left rather than the count it started with, so a doubled count is a doubled duration
    ///     and nothing else changes. A count of zero is the helper's instant assignment and is
    ///     passed through untouched.
    /// </summary>
    public bool ChShadeRamps { get; init; } = true;

    /// <summary>
    ///     Divide the per-call turn rate of the scripted element pitch, so an actor tilts over the
    ///     time the script asked for instead of half of it.
    ///
    ///     ATEL <c>?setPitch [506Dh]</c> takes a target angle and a rate in degrees per call, and
    ///     Ch_CalcElement steps the actor's element pitch towards the target by at most that much
    ///     once per presented frame. 60 authored call sites in 9 event scripts, rates of 3, 5, 10,
    ///     15, 30 and 45 degrees - so up to 1350 degrees a second at 30 Hz and 2700 at 60.
    ///
    ///     The 30 sites that pass a rate of zero are snaps and are passed through: the setter's zero
    ///     branch assigns the target to the current angle, so scaling it would turn a hard cut into
    ///     a one-frame ramp. Its own switch rather than part of
    ///     <see cref="ChFacingTurnRate"/> because that one is every actor in the game and this one
    ///     is nine scripted scenes, so they have to be separable in a bisection.
    /// </summary>
    public bool ChPitchTurnRate { get; init; } = true;

    /// <summary>
    ///     Let loose files on disk win over the VBF archive, so an asset can be replaced without
    ///     rebuilding 20.7 GB.
    ///
    ///     The engine already opens loose files - its fios open asks BigFileStream::openFile first
    ///     and falls through to CreateFileW on the same relative path when that returns null. All
    ///     this does is make the archive answer no for paths the override tree carries. Nothing
    ///     fabricates a file handle, which is why an override has to sit at exactly the relative
    ///     path the engine asks for: the fallback receives that path unchanged.
    ///
    ///     The two hooks are on the path every file open takes, so they are installed only when the
    ///     override tree actually holds something. An empty tree costs nothing.
    ///
    ///     <para>Off by default, because installing the hooks crashed the process twice.</para>
    ///     Both runs died with an access violation reported against the frame hook, once at the
    ///     first video and once in normal play twenty seconds in, while the last run without the
    ///     hooks installed played for 653 seconds including videos. The two detours are the only
    ///     thing that differed. Their measured convention is not in doubt but the detour itself is,
    ///     and a crash that lands in an unrelated frame is what a stack imbalance looks like. Turn
    ///     this on only to work on that.
    /// </summary>
    public bool AssetOverride { get; init; } = false;

    /// <summary>
    ///     Plays optional AV1 WebM/MKV movies from the mod's video directory or existing override tree.
    ///     Localized filenames precede shared US/JP files; missing or rejected candidates retain
    ///     original playback. An explicitly configured FMV lab takes precedence.
    /// </summary>
    public bool VideoOverride { get; init; } = true;

    /// <summary>Optional immutable FMV test cases, selected for the next normal movie initialization. Starts with retail.</summary>
    public string? FmvLabManifest { get; init; }

    /// <summary>Allow VP8 lab candidates up to 2560x1440 and 120000/1001 FPS. Native playback remains unverified.</summary>
    public bool FmvLabExperimentalFormats { get; init; }

    /// <summary>
    ///     Which subtrees of the game directory may hold overrides, searched in order.
    ///
    ///     They have to be under the game directory and at the asset's own relative path, because
    ///     the engine's CreateFileW fallback receives that path unchanged - an override lives at,
    ///     for example, <c>ffx_data/gamedata/ps3data/video/opk_us.webm</c>. A tidier root such as
    ///     <c>data/mods</c> would need this module to open the file and hand back a VFile of its
    ///     own, which is a great deal more machinery for a cosmetic gain.
    ///
    ///     <para>The default is the three archive prefixes and not the game directory itself.</para>
    ///     Those three are exactly what the VBF indexes contain, and scoping to them is a
    ///     correctness requirement rather than a speed one: rooted at the game directory this would
    ///     index FFX.exe, the fahrenheit tree and the archives themselves, and then claim any of
    ///     them the engine happened to ask for - including telling the loader that
    ///     <c>data/ffx_data.vbf</c> is not in the archive.
    /// </summary>
    public string[] AssetOverrideRoots { get; init; } = ["ffx_data", "ffx2_data", "metamenu"];

    /// <summary>
    ///     Scale move type 9's ten-pass lead-in, the wind-up an object waits out before it starts
    ///     interpolating.
    ///
    ///     The threshold at worker+0xb16 has one writer in the image and no ATEL opcode behind it, so
    ///     none of the deadline setters reaches it: at 60 Hz the wind-up lasts 166 ms against an
    ///     authored 333 ms. An image patch on the immediate rather than a hook on the initialiser,
    ///     because the catalog's calling convention for that function is a default rather than a
    ///     measurement.
    /// </summary>
    public bool AtelWorkerLeadIn { get; init; } = true;

    /// <summary>
    ///     Correct the position integrator of the ATEL workers that are not bound to a Ch
    ///     character: the field and event camera, and every map group or map part a script
    ///     attaches. It adds the worker's speed along its facing to its position once per call with
    ///     no delta time at all, so at 60 Hz all of them travel twice as fast.
    ///
    ///     Separate from <see cref="AtelWorkerMotion"/> even though it belongs to the same records,
    ///     because it is the largest visible change of the four and the only one that moves the
    ///     camera itself. Bisecting it should not mean turning off eleven corrections that are not
    ///     in question.
    ///
    ///     The correction scales the speed the integrator reads and puts it back afterwards rather
    ///     than scaling the setter that wrote it. The setter is the wrong lever twice over: the
    ///     same field feeds Ch_SetSp for the 5,509 loadModel workers whose motion Ch_CalcMain
    ///     already integrates against a corrected delta, and the speed the integrator actually uses
    ///     is recomputed every call as a proportional approach or an accelerated ramp rather than
    ///     being the stored value at all.
    /// </summary>
    public bool AtelWorkerTranslation { get; init; } = true;

    /// <summary>
    ///     Correct the particle spawn cadence of the twenty magic overlays that gate their emission
    ///     on sg_count, by handing them a counter that advances once per authored 30 Hz step instead
    ///     of once per presented frame.
    ///
    ///     Their gate is <c>sg_count &amp; 3</c> or <c>sg_count &amp; 7</c> around the code that
    ///     allocates a particle, so one emission every four or every eight frames. At 60 Hz they emit
    ///     at double density. Nothing else in this module reaches them: all twenty have a stub
    ///     advance, and the gates sit in draw-side step kernels that neither the effect advance nor
    ///     the particle kernel hold touches.
    ///
    ///     The correction repoints gMagicFunctions slot 764, which is where the overlays get the
    ///     address from, so no overlay is modified and no other reader of sg_count is affected. It is
    ///     not a hold: the overlays keep running every frame and only the clock they read is retimed.
    /// </summary>
    public bool OverlaySpawnGate { get; init; } = true;

    /// <summary>Log measured present rate and frame delta statistics.</summary>
    public bool Telemetry { get; init; } = true;

    /// <summary>Record a bounded local JSONL session with frame state and diagnostic markers.</summary>
    public bool Diagnostics { get; init; } = true;

    /// <summary>Record bit-preserving phase and memory samples in bounded compressed binary blocks.</summary>
    public bool DiagnosticsBinary { get; init; }

    public bool TextureBlendProbe { get; init; }

    public bool TextureFrameInterpolation { get; init; }

    /// <summary>Capture read-only viewport and camera-commit phases when validated hooks are available.</summary>
    public bool NativePhaseProbe { get; init; } = true;

    /// <summary>Session file ceiling in MiB, clamped to 8..2048. Loss is reported in session_end.</summary>
    public int DiagnosticsMaxMiB { get; init; } = 512;

    /// <summary>
    ///     Draws an on-screen clock, matching the log's own timestamp format, plus a compact
    ///     counter view. A diagnostic aid for play-testing, not a gameplay feature: it lets a
    ///     visual defect that leaves no log line of its own be timed against the log by eye.
    /// </summary>
    public bool TimerOverlay { get; init; }

    /// <summary>
    ///     Log the restart countdown and the two end-condition flags of every live field particle
    ///     manager, once a second. A diagnostic probe, not a correction: it reads engine memory and
    ///     writes a log line, and changes nothing about how anything is timed.
    ///
    ///     It settles which of the two remaining causes makes field particle groups vanish and pop
    ///     back in. pppFpLoop decrements a countdown at manager+0x00 once per pass with no time term
    ///     and skips the group entirely while it is not yet negative, so that duration halves in
    ///     wall clock at twice the pass rate. The other candidate is pppCheckViewCylinder, which is
    ///     camera driven and rate independent. A run in which every live manager reads -1 throughout
    ///     rules out the countdown and leaves the cylinder.
    /// </summary>
    public bool FieldManagerProbe { get; init; }

    internal static int NormalizeSimulationFramerate(int requested, int previous)
        => requested == 90 ? previous >= 120 ? 60 : 120 : requested < 45 ? 30 : requested < 90 ? 60 : 120;

    internal static int NormalizeMaxFramerate(int rate, int previous = 0) => rate == 0 || (rate == 29 && previous == 30) ? 0 : Math.Max(30, rate);

    internal static bool IsSupportedTargetFramerate(double rate) => rate is 30 or 60 or 120;

    internal double? ResolveTargetFramerate(int panelRate, out string? warning)
    {
        warning = null;
        if (TargetFramerateOverride is { } configured) return configured;
        if (panelRate == 0) return null;
        if (IsSupportedTargetFramerate(panelRate)) return panelRate;
        warning = $"Panel target framerate {panelRate} is not one of 30/60/120; using automatic correction-rate selection.";
        return null;
    }

    /// <summary>Read failures return defaults. Use the three-argument overload to receive validation warnings.</summary>
    public static VrrConfig Load(string path, out string? error) => Load(path, out error, out _);

    /// <summary>
    ///     Read failures return defaults and an error. Invalid overrides are ignored with a warning;
    ///     all other successfully parsed settings remain applied.
    /// </summary>
    public static VrrConfig Load(string path, out string? error, out string? warning)
    {
        error = null;
        warning = null;

        VrrConfig cfg;
        if (!File.Exists(path)) cfg = new VrrConfig();
        else
        {
            try
            {
                cfg = JsonSerializer.Deserialize<VrrConfig>(File.ReadAllText(path)) ?? new VrrConfig();
            }
            catch (Exception e)
            {
                error = e.Message;
                return new VrrConfig();
            }
        }

        if (cfg.TargetFramerateOverride is { } tf && !IsSupportedTargetFramerate(tf))
        {
            warning = $"TargetFramerateOverride {tf} is not one of 30/60/120; override ignored. Other settings remain applied; panel or automatic correction-rate selection is used.";
            cfg = cfg with { TargetFramerateOverride = null };
        }

        return cfg;
    }

    /// <summary>
    ///     Where the config actually sits, which is beside this assembly in the mod's own
    ///     directory. AppContext.BaseDirectory is the host's bin directory, not the mod's, so a
    ///     config written next to the DLL was silently never read and every run took the defaults.
    /// </summary>
    public static string ResolvePath()
    {
        var assembly = typeof(VrrConfig).Assembly.Location;

        if (!string.IsNullOrEmpty(assembly) && Path.GetDirectoryName(assembly) is { Length: > 0 } dir)
            return Path.Combine(dir, FileName);

        return Path.Combine(AppContext.BaseDirectory, FileName);
    }

    public const string FileName = "fhvrr.config.json";
}
