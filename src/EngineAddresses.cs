namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Hook targets and globals, as RVAs relative to the FFX.exe image base.
///
///     The pinned Fahrenheit's STEP generation exposes most engine functions only as
///     <c>FUN_&lt;address&gt;</c> entries, so hooks are addressed numerically rather than by
///     the friendly FhCall names. Values are derived from the knowledge base function
///     catalog, whose addresses are Ghidra VAs: RVA = VA - 0x400000.
/// </summary>
public static class EngineAddresses
{
    // --- Present and frame loop ---

    /// <summary>
    ///     FFXApplication::animate. Thiscall, returns uint. The game's per-frame entry point: it runs
    ///     the Steam callbacks, calls updateFFX (which drives the main loop and Sg_MainLoop) and then
    ///     chains into Phyre::PFramework::PApplication::animate.
    /// </summary>
    public const nint FFXApplicationAnimate = 0x2F520;

    /// <summary>
    ///     updateFFX. Cdecl, takes the frame delta. The main loop body itself; it repeats while
    ///     gElapsedFrameCount_frameSkip is non-zero without recomputing the delta.
    /// </summary>
    public const nint UpdateFFX = 0x422840;

    /// <summary>Phyre::PFramework::PWindowWin32Base::SetFlipVSyncInterval. Cdecl.</summary>
    public const nint SetFlipVSyncInterval = 0x225090;

    /// <summary>Sg_MainCalcRate. Computes sg_rate from the vertical/horizontal blank ratio each frame.</summary>
    public const nint SgMainCalcRate = 0x4206C0;

    /// <summary>
    ///     The frame-pacing routine that advances the vertical blank counters. It adds a hardcoded 2
    ///     to both, which is the engine's assumption of two vertical blanks per 30 Hz frame.
    /// </summary>
    public const nint AdvanceVBlankCounters = 0x421E80;

    /// <summary>
    ///     iSyncGetData(float* total, float* next, int* rate). Cdecl. The one source of the syncdata
    ///     pacing: FUN_00821E80 calls it once per frame and reads the return value, setting
    ///     g_isNeedSync when it is anything but 999 and clearing the whole sync state when it is 999.
    ///
    ///     Ghidra types the return as void; the caller's `iVar1 = iSyncGetData(...); if (iVar1 ==
    ///     999)` is what says otherwise, and the caller is the authority.
    ///
    ///     999 is the engine's own "this scene has no recording" answer, and taking that path clears
    ///     g_isNeedSync, both PS2 time accumulators, the rate index and the 0x78-byte rate table. So
    ///     answering 999 disables the recorded pacing through the door the engine already has, rather
    ///     than by writing a state it would not otherwise be in.
    /// </summary>
    public const nint ISyncGetData = 0x27A930;

    /// <summary>Sg_SetKeepFps. Cdecl, sbyte in, returns the previous value.</summary>
    public const nint SgSetKeepFps = 0x421AE0;

    /// <summary>
    ///     Sg_GetKeepFps. Cdecl, no parameters, returns the sbyte at <see cref="SgKeepFps"/>. Its
    ///     only non-debug caller is the motion advance; the other three are the debug window's
    ///     label, its toggle, and yiAnimInfo_init.
    /// </summary>
    public const nint SgGetKeepFps = 0x4205A0;

    /// <summary>
    ///     yiAnimInfo_init(info). Stdcall. Picks the clock the event worker's animation curves run
    ///     on and stores it in yi_anim_clock_mode, once, from whatever KEEP_FPS happens to be.
    /// </summary>
    public const nint YiAnimInfoInit = 0x514310;

    /// <summary>
    ///     The clock mode itself. Zero routes the worker through yiGetFCount, which is sg_count * 2
    ///     and advances two units per presented frame with nothing correcting it; non-zero routes it
    ///     through yiGetVCount, whose advance this module already rescales.
    /// </summary>
    public const nint YiAnimClockMode = 0x153BB08;

    /// <summary>
    ///     sbyte. The KEEP_FPS flag itself, which Sg_GetKeepFps returns. Read directly rather than
    ///     mirrored through the setter, so a write this module does not see cannot desynchronise it.
    /// </summary>
    public const nint SgKeepFps = 0xEFBC34;

    // --- Character and motion ---

    /// <summary>
    ///     The per-actor motion advance, FUN_00838d10(actor, mode). Cdecl. It adds the actor's
    ///     motion speed into the frame accumulator at actor+0x740, and applies sg_rate only when
    ///     the actor's flag word at +0x194 carries 0x100000 and KEEP_FPS is set.
    /// </summary>
    public const nint MotionAdvance = 0x438C90;

    /// <summary>The actor's flag word, tested by the motion advance for 0x40 and 0x100000.</summary>
    public const int ActorFlagsOffset = 0x194;

    /// <summary>Ch_CalcMain(float delta). The engine passes a fixed delta of 0.033373334.</summary>
    public const nint ChCalcMain = 0x432E10;

    /// <summary>Zero-argument job wait, called after buoyancy and before character worker dispatch.</summary>
    public const nint CharacterJobBarrier = 0x644B80;

    /// <summary>
    ///     chr. Pointer-valued global holding the base of the Ch work record array, one 0x880 byte
    ///     record per slot. Allocated once by the scene init and never moved, so a record address
    ///     taken during a call stays valid for that call.
    /// </summary>
    public const nint ChrArray = 0x1FC44E4;

    /// <summary>
    ///     nb_maxchr. int, the number of Ch work records. Every per-actor loop in the engine runs
    ///     from chr to chr + nb_maxchr * 0x880 with that stride; the allocation is twice that size,
    ///     so the count is the loop bound rather than the allocation size.
    /// </summary>
    public const nint NbMaxChr = 0x1FC44E0;

    /// <summary>Ch_SetMotionSpeed(Actor*, ushort). Animation speed follows motion speed unless KEEP_FPS is set.</summary>
    public const nint ChSetMotionSpeed = 0x42B380;

    /// <summary>
    ///     The walk motion controller, the first of Ch_CalcMain's two loop-1 controllers. Selects one
    ///     of three per-call facing rates by the actor's speed and steps Chr+0x158 towards Chr+0x168
    ///     by it; the rest of the function picks the motion or sequence to play.
    ///
    ///     Not hooked. The three rate loads inside it are patched at their operands; see
    ///     VrrModule.ChFacing.cs for why neither the function nor the literal pool is the right
    ///     place.
    /// </summary>
    public const nint ChWalkMotionController = 0x435BB0;

    /// <summary>
    ///     The swim motion controller, the second one. Carries the same three-way facing selection
    ///     with the same three constants, and additionally the 0.15 per call stroke impulse on
    ///     Chr+0x504 that the buoyancy hold already brackets.
    /// </summary>
    public const nint ChSwimMotionController = 0x435D30;

    /// <summary>
    ///     <c>void(Chr* actor, float angle, float degreesPerCall)</c>, the arm behind ATEL
    ///     <c>?setPitch [506Dh]</c>. Stores the wrapped target at Chr+0x160 and the rate at Chr+0x164
    ///     as radians, and Ch_CalcElement steps Chr+0x15c towards the target by at most that much per
    ///     presented frame, applying the result as an X rotation on the element matrix.
    ///
    ///     A rate of zero takes the other branch: it assigns the target to the current angle as well
    ///     and stores zero, which is a snap rather than a slow turn. Measured cdecl, one caller.
    /// </summary>
    public const nint ChSetPitchTarget = 0x4357F0;

    /// <summary>
    ///     <c>bool __thiscall BigFileStream::checkExists(BigFileStream*, const char* path)</c>. MD5-hashes
    ///     the already-lowercased path and looks the digest up in the five archive slots. Its caller
    ///     reaches CreateFileW on the real filesystem only when this returns zero.
    ///
    ///     Measured thiscall: 412 bytes ending in ret 4, one stack argument, security cookie prologue.
    ///     The catalog records __stdcall with one parameter, which is its default rather than a
    ///     measurement.
    /// </summary>
    public const nint BigFileCheckExists = 0x21BE40;

    /// <summary>
    ///     <c>VFile* __thiscall BigFileStream::openFile(BigFileStream*, const char* path)</c>, the open
    ///     half of the same pair and the one the fios open calls first. A null return is what makes it
    ///     fall through to CreateFileW on the same relative path, storing the two results side by side
    ///     as handle_vbf and handle_os.
    ///
    ///     Measured thiscall: 204 bytes ending in ret 4, path at [ebp+8], same cookie prologue.
    /// </summary>
    public const nint BigFileOpenFile = 0x21BF10;

    /// <summary>
    ///     The arm for all seven of an actor's <c>{current, target, frames}</c> ramp records at
    ///     Chr+0x330, stride 0xc: <c>void(float* record, float target, int frames)</c>. Writes the
    ///     target and the count, and assigns the target outright when the count is zero. Measured
    ///     cdecl.
    ///
    ///     Its seven call sites are Ch_SetShadeCol (three, one per colour channel), Ch_SetTransparent,
    ///     Ch_SetShade, Ch_SetShadeID and Ch_SetSpecularAlpha, so it is the whole arm surface of the
    ///     character fade system. The stepper is FUN_008360a0, called seven times per actor per
    ///     presented frame from Ch_CalcMain.
    /// </summary>
    public const nint ChSetRampTarget = 0x42B990;

    /// <summary>
    ///     Unnamed motion parameter setter: (chr_id, stat_id, float value). stat ids 0x03..0x06 are
    ///     MOTION_RUN_SPEED, _RETURN, _V0 and _ACC. Reached from ATEL call targets 70A8 and 70B2.
    /// </summary>
    public const nint SetMotionParamFloat = 0x3B4A90;

    /// <summary>MsSetChrStatInfo(chr_id, stat_id, target_id, value). Reached from ATEL call targets 70AB and 7018.</summary>
    public const nint MsSetChrStatInfo = 0x0;

    /// <summary>MsEffectSetSpeed(byte chr_id, ushort speed).</summary>
    public const nint MsEffectSetSpeed = 0x388430;

    // --- Camera ---

    /// <summary>MsCameraMoveFrame(camera_id, _, _, frame_count, _). Cutscenes synchronise on camWait.</summary>
    public const nint MsCameraMoveFrame = 0x3BDE50;

    /// <summary>MsCameraMoveAcc(camera_id, mode_non_ref, mode_polar, + four timing arguments).</summary>
    public const nint MsCameraMoveAcc = 0x3BD860;

    // --- Fades, flash, alpha ---

    /// <summary>
    ///     The common fade routine behind Sg_FadeIn/InW/Out/OutW: (frame_count, mode_in, mode_w).
    ///     Reached from ATEL call targets 4004 through 4007.
    /// </summary>
    public const nint SgFadeCommon = 0x42CDC0;

    /// <summary>Sg_Flash(frame_count, ...). ATEL call target 4003.</summary>
    public const nint SgFlash = 0x42CCA0;

    /// <summary>
    ///     Sg_AccSetAlpha(alpha, frame_count). Cdecl, measured: the body ends in a tail jmp to
    ///     graphicUpdateCrossFade after a plain pop ebp, and that target cleans through the caller.
    ///     ATEL call target 401A, where the first pushed argument is the alpha and the second the
    ///     frame count, the opposite of what the script dumps label them.
    ///
    ///     It seeds filter slot 0 rather than expressing a duration: the stored step is
    ///     max(1, |alpha - current| / frame_count), so a doubled frame count cannot buy a half step
    ///     once |delta| drops below it. See VrrModule.CrossFade.
    /// </summary>
    public const nint SgAccSetAlpha = 0x42BD10;

    /// <summary>
    ///     Sg_DrawFilter(). Cdecl and void, measured: a plain c3 at 0x0082C277, closing the
    ///     8b e5 5d c3 epilogue at 0x0082C274, and its one call
    ///     site in Sg_MainLoop pushes nothing and cleans nothing. Runs once per main loop pass and
    ///     steps all five filter slots.
    /// </summary>
    public const nint SgDrawFilter = 0x42C0C0;

    /// <summary>
    ///     int. Bit mask of the five filter slots (screen fade, flash, cross-fade) that Sg_DrawFilter is
    ///     still stepping; it clears a slot's bit when that slot's counter reaches zero. Read from the
    ///     first compare in Sg_DrawFilter (cmp [0x01300704], 0 in the installed executable).
    /// </summary>
    public const nint FilterActiveMask = 0xF00704;

    /// <summary>
    ///     Four dwords Sg_DrawFilter writes for its consumers: two flags, a latch and a progress float
    ///     (mov dword [0x012FFAE8], 1 / fstp [0x012FFAF4] at Sg_DrawFilter+0x28F). v3 0x012FFAA0.
    /// </summary>
    public const nint FilterOutputs = 0xEFFAE8;

    /// <summary>uint. Fade type Sg_DrawFilter publishes (fadeType, mov [0x00C496D0], eax at +0x28A).</summary>
    public const nint FilterFadeType = 0x8496D0;

    /// <summary>byte. Btl.battle_state, 0 outside battle. Btl sits at 0xD2A8D0; the sibling Parry mod gates on it.</summary>
    public const nint BattleState = 0xD2A8E0;

    /// <summary>TkSetFadeOut(frame_count). ATEL call target 00BB; also reachable outside it.</summary>
    public const nint TkSetFadeOut = 0x48EB50;

    // --- ATEL ---

    /// <summary>
    ///     Init handler of ATEL call target 0000 ("wait", 53,676 call sites), the frame-based wait.
    ///     One-frame waits are idle loops and must not be scaled.
    /// </summary>
    public const nint AtelWaitInit = 0x45C4E0;

    /// <summary>
    ///     Exec handler of ATEL call target 0000, called once per pass with the counter the init
    ///     handler stored. Cdecl, two arguments, of which only the second (the counter) is read;
    ///     both returns are a plain c3.
    ///
    ///     It normally decrements the counter by one, so scaling the counter at init is the whole
    ///     correction. It does not while gMoviePlay is set and movie_have_camera agrees: there it
    ///     subtracts the movie-frame delta at 0x245F40 instead, which is wall-clock and therefore
    ///     already at the right rate. A counter scaled at init then takes twice as many movie
    ///     frames to run out.
    /// </summary>
    public const nint AtelWaitExec = 0x45C6B0;

    /// <summary>
    ///     Movie frames elapsed since the FMV manager last latched its own frame number:
    ///     PhyFMVPlayerManager+0x6D8 minus +0x6E0. Cdecl, no arguments, and a pure read - the two
    ///     fields are written elsewhere, so calling it has no effect on anything.
    ///
    ///     Its other caller is the ATEL camera interpolator at 0x468930, which adds the same delta
    ///     to a float frame accumulator under the same movie-camera condition.
    /// </summary>
    public const nint MovieFrameDelta = 0x245D90;

    /// <summary>AtelPopStackInteger(worker, stack).</summary>
    public const nint AtelPopStackInteger = 0x46DF00;

    // --- ATEL worker move and rotation records ---
    //
    // Every setter below is a leaf with one caller, its own ATEL opcode handler, and every one of
    // them ends in 5d c3 rather than a c2 imm16, so the caller cleans: cdecl, measured in the
    // shipped image rather than taken from the catalog, which reports __stdcall for all of them.
    //
    // The two readers and the integrator report an unreadable cleanup in the catalog because their
    // returns are not a plain ret. Their shared call site settles it instead: FUN_00866680 at
    // 0x0086671b pushes two arguments for each of the first two calls and clears sixteen bytes
    // once, and pushes two for each of the next four and clears thirty-two once. Caller cleanup,
    // two arguments, for all three.

    /// <summary>
    ///     Move record reader, void(worker, thread). Cdecl, two arguments. Reads the record at
    ///     thread+0x44 once per ATEL pass and advances everything in it: the turn steps, the
    ///     watchdog counter, the interpolation accumulator and the actor's speed. Its prologue
    ///     carries a stack cookie (a1 d8 13 c6 00 33 c5 89 45 fc), which the entry detour does not
    ///     disturb - the cookie is checked against the frame the original itself sets up.
    /// </summary>
    public const nint AtelMoveRecordRead = 0x468990;

    /// <summary>
    ///     Rotation record reader, void(worker, thread). Cdecl, two arguments, no stack cookie.
    ///     Ghidra types the second parameter float; it is the same thread pointer the move reader
    ///     takes, dereferenced as an int immediately (iVar6 = (int)param_2, then iVar6 + 0x48).
    ///     A binding built from the Ghidra signature marshals it through the floating point path.
    /// </summary>
    public const nint AtelRotRecordRead = 0x469510;

    /// <summary>
    ///     Position integrator for ATEL workers that are not bound to a Ch character,
    ///     void(worker, thread). Cdecl, two arguments; the catalog declares one, and the call site
    ///     pushes two.
    ///
    ///     It runs for worker+0xaa in 2..5 - attachToCamera, attachToMapGroup, attachToMapPart -
    ///     and adds the actor's speed along its two Euler angles to its position once per call with
    ///     no delta time, then hands the result to MsCameraSetRect for kind 2. Kind 1, the
    ///     loadModel character, leaves through Ch_SetSp instead and Ch_CalcMain integrates it
    ///     against the corrected delta, so this is the only translation path the module does not
    ///     otherwise reach.
    /// </summary>
    public const nint AtelWorkerIntegratePos = 0x4629C0;

    /// <summary>
    ///     movie_have_camera. Cdecl, no arguments, a pure read: true only outside the Luca theatre
    ///     and only for curMovieId 0x2e, 0x48, 0x20 or 0x46. It is the second half of the gate that
    ///     tells the move reader's type 7 to advance by the FMV decoder's own frame delta rather
    ///     than by one, and the module evaluates it rather than inferring it from the delta.
    /// </summary>
    public const nint MovieHaveCamera = 0x36EFC0;

    // Group A, the twelve per-call rate setters. Each writes a rate in radians per call, so each is
    // divided by the scale.

    /// <summary>ATEL setYawTurnStepAllLevels [006Dh] -> move+0x3c on all nine levels. Cdecl.</summary>
    public const nint AtelSetYawTurnStepAllLevels = 0x46F1C0;

    /// <summary>ATEL setPitchTurnStepAllLevels [006Eh] -> move+0x40 on all nine levels. Cdecl.</summary>
    public const nint AtelSetPitchTurnStepAllLevels = 0x46F170;

    /// <summary>
    ///     ATEL setGravity [0094h] -> move+0x34, the per-call velocity increment, on the calling
    ///     thread's own priority level. Cdecl, two arguments like the all-levels rate setters: it
    ///     reads the level out of worker+0x32 rather than taking it.
    ///
    ///     Read in exactly two places, both in the move reader's type 3 and 4 tail: as the increment
    ///     in v += a, and as the brake ceiling in sqrt(2 a d). Those want different scale exponents,
    ///     so no single factor at this setter is right for both - see the AtelWorkerGravity switch,
    ///     which is off for that reason. Move type 9 reads the field a third time, as the quadratic
    ///     coefficient of an arc over normalised progress, where it is not frame-bound at all.
    ///
    ///     It is not the acceleration of a thrown blitzball. The blitzball script corpus starts only
    ///     move types 1 and 2, neither of which reads this field.
    /// </summary>
    public const nint AtelSetMoveGravity = 0x46F040;

    /// <summary>
    ///     The <c>mov eax, 0xA</c> inside AtelInitBasicWork that feeds the only write of
    ///     worker+0xb16, move type 9's lead-in threshold in passes. Patched rather than hooked; see
    ///     VrrModule.MoveLeadIn.cs.
    /// </summary>
    public const nint AtelMoveLeadInLoad = 0x46CE55;

    /// <summary>ATEL setYawTurnStep [002Bh] -> move+0x3c on one level. Cdecl, three arguments.</summary>
    public const nint AtelSetYawTurnStep = 0x471520;

    /// <summary>ATEL setPitchTurnStep [002Ch] -> move+0x40 on one level. Cdecl, three arguments.</summary>
    public const nint AtelSetPitchTurnStep = 0x471500;

    /// <summary>ATEL setAllRotationRate1 [006Fh] -> rot+0x10, the yaw rate, on all nine levels. Cdecl.</summary>
    public const nint AtelSetYawRotRateAllLevels = 0x46F2B0;

    /// <summary>ATEL [00E4h] -> rot+0x14, the alternate yaw rate flag 0x200 selects, on all nine levels. Cdecl.</summary>
    public const nint AtelSetAltYawRotRateAllLevels = 0x46F300;

    /// <summary>ATEL setAllRotationRate2 [0070h] -> rot+0x18, the pitch rate, on all nine levels. Cdecl.</summary>
    public const nint AtelSetPitchRotRateAllLevels = 0x46F260;

    /// <summary>ATEL setAllRotationRate3 [0071h] -> rot+0x1c, the roll rate, on all nine levels. Cdecl.</summary>
    public const nint AtelSetRollRotRateAllLevels = 0x46F210;

    /// <summary>ATEL setRotationSpeed1 [002Eh] -> rot+0x10 on one level, 8,440 script sites. Cdecl, three arguments.</summary>
    public const nint AtelSetYawRotRate = 0x471580;

    /// <summary>ATEL [00E3h] -> rot+0x14 on one level. Cdecl, three arguments.</summary>
    public const nint AtelSetAltYawRotRate = 0x4715A0;

    /// <summary>ATEL setRotationSpeed2 [002Fh] -> rot+0x18 on one level, 9,451 script sites. Cdecl, three arguments.</summary>
    public const nint AtelSetPitchRotRate = 0x471560;

    /// <summary>ATEL setRotationSpeed3 [0030h] -> rot+0x1c on one level. Cdecl, three arguments.</summary>
    public const nint AtelSetRollRotRate = 0x471540;

    // Group B, the four watchdog deadlines. Each is a frame count stored sixteen bits wide, so each
    // is multiplied by the scale and clamped to what a short can hold.

    /// <summary>
    ///     ATEL setTurningDuration [0074h] -> move+0x04 on all nine levels, 736 script sites, 726
    ///     of which pass 60. Cdecl; the argument arrives as a full dword (8b 4d 0c) and is stored
    ///     sixteen bits wide (66 89).
    /// </summary>
    public const nint AtelSetTurnDeadlineAllLevels = 0x46F080;

    /// <summary>ATEL [0072h] -> move+0x04 on one level, no script sites. Cdecl, three arguments.</summary>
    public const nint AtelSetTurnDeadline = 0x470520;

    /// <summary>ATEL [0075h] -> rot+0x04 on all nine levels, no script sites. Cdecl.</summary>
    public const nint AtelSetRotDeadlineAllLevels = 0x46F120;

    /// <summary>ATEL [0073h] -> rot+0x04 on one level, no script sites. Cdecl, three arguments.</summary>
    public const nint AtelSetRotDeadline = 0x471170;

    // --- Battle, menu, particles, FMV ---

    /// <summary>TOBtlCtrlLimitTimer. Divides by a fixed 30 or 25 depending on the mode.</summary>
    public const nint TOBtlCtrlLimitTimer = 0x491AC0;

    /// <summary>rnd. Used by the limit timer to jitter its last displayed digit.</summary>
    public const nint Rnd = 0x3989A0;

    /// <summary>pppFpStopStatus. Particle timing is self-driven, so particles are stopped rather than retimed.</summary>
    public const nint PppFpStopStatus = 0x32A750;

    /// <summary>
    ///     _pppStartPart(manager, time_step, data, flags). Cdecl. Starts one particle manager and
    ///     writes time_step into its +0x10, which _pppRunPart adds to the accumulator at +0x08 every
    ///     frame. It is the only place a manager's step is set, and it is where every source of a
    ///     step converges: the global ppv step, and the literal 0x1000 the magic path passes.
    /// </summary>
    public const nint PppStartPart = 0x3123C0;

    /// <summary>
    ///     pppPartLoop. Runs every active manager and calls pppDataRcv at its end, which is where the
    ///     broadcast lives that writes the global step into every manager's +0x10.
    /// </summary>
    public const nint PppPartLoop = 0x362240;

    /// <summary>
    ///     _pppRunPart(manager, mode). Cdecl, returns a status. The single choke point of the
    ///     particle advance: pppPartLoop is only one of its eight callers, and the cutscene and
    ///     battle paths reach it without going through the loop at all.
    /// </summary>
    public const nint PppRunPart = 0x312250;

    /// <summary>
    ///     _pppRunPartFp(manager, mode). Cdecl, returns a status. The field half of the particle
    ///     system, reached from pppFpLoop by way of yiCallFieldParticle. A cutscene's particles go
    ///     through this and never through _pppRunPart.
    /// </summary>
    public const nint PppRunPartFp = 0x3122F0;

    /// <summary>
    ///     pppFpLoop. Void, no arguments, and it ends on a plain ret, so the caller cleans. The field
    ///     particle pass: one iteration per group, the view cylinder test, then the advance and the
    ///     draw, both of which are built into the same packet the function measures afterwards.
    ///
    ///     It carries a restart countdown at each manager's +0x00 which it decrements once per pass
    ///     with no time term: while the result is still zero or above the group is skipped entirely,
    ///     and at -1 every object is freed and the group is started again.
    /// </summary>
    public const nint PppFpLoop = 0x329AB0;

    /// <summary>
    ///     MsEffectProcess(mode). Cdecl, measured: every ret in the function is a plain c3.
    ///
    ///     Mode 0 advances every active effect and mode 1 draws them, and the discrimination holds
    ///     through the whole body rather than only its head. All three dispatch sites - the eternal
    ///     table at ot_eot_eternal, the per-actor loop over the 31 Chr slots whose +0xDFB is 4 or 5,
    ///     and the single non-actor overlay at 0x01133360 - each pick the overlay's +0xC on mode 0
    ///     and its +0x10 on mode 1. The timeline itself is inside the DLL, which is why no function
    ///     in FFX.exe carries it.
    ///
    ///     The advance runs exactly once per Sg_MainLoop pass. Sg_MainLoop wraps it in a loop over
    ///     Sg_GetCurExecFrames and repeats it once more where that reads zero, but neither is a real
    ///     multiplier: outside debug mode Sg_GetCurExecFrames is _ExecFrames, which ships as 1 and
    ///     whose only non-debug writer is Sg_SetExecFrames(1) at battle init, so the loop runs once
    ///     and the zero branch is dead. The engine's real multi-pass is one level up, updateFFX
    ///     repeating the whole main loop while <see cref="ElapsedFrameCountFrameSkip"/> is non-zero.
    ///     One pass per presented frame is therefore the normal case, and at 60 Hz that is twice the
    ///     rate the effects were authored for.
    /// </summary>
    public const nint MsEffectProcess = 0x387E00;

    /// <summary>
    ///     _player_chrs, the base of the battle Chr array, which MsGetChrTop returns unchanged. Zero
    ///     outside battle, which is what makes the per-actor half of MsEffectProcess inert there.
    /// </summary>
    public const nint PlayerChrs = 0xD334D4;

    /// <summary>
    ///     The non-actor effect overlay's state byte, the global counterpart of Chr+0xdfb. Values 4
    ///     and 5 mean an overlay is live and MsEffectProcess dispatches into it on both modes.
    /// </summary>
    public const nint GlobalEffectOverlayState = 0xD33364;

    /// <summary>
    ///     The old-format character texture animation advance. Dispatched per slot from
    ///     FUN_0077c9c0 on descriptor byte 2; the new format goes to FUN_0077f450 instead.
    ///     Its counters are literal increments - a sprite step of +1 per call and a blink
    ///     countdown of rand() % 0x5a + 0x3c - so it carries nothing to scale.
    /// </summary>
    public const nint ChrTexAnimAdvanceOld = 0x37FF00;

    /// <summary>Runs the first static effect list with native scratch setup and commit. Cdecl void().</summary>
    public const nint EternalEffectRunBefore = 0x400610;
    /// <summary>
    ///     The eternal effect set's second entry point, efftOverTbl+0x10. Ghidra names the pair
    ///     op_effect_run_before and op_effect_run_after: both run FUN_0080cd60 over a different
    ///     object list, so neither is a draw and both decrement the per-channel wait bytes.
    /// </summary>
    public const nint EternalEffectRunAfter = 0x400670;

    /// <summary>
    ///     KeLnsShp_Update(work). The sprite frame clock of the lens and flare family: adds a fixed
    ///     0x200 to an accumulator per call and compares it against the current frame's duration.
    ///     Its four callers are the four Lns draw kernels, so it advances once per drawn frame.
    /// </summary>
    public const nint KeLnsShpUpdate = 0x36ABE0;

    /// <summary>
    ///     Ch_SeqFrame(work). The motion sequence VM. Its wait opcode counts raw calls at
    ///     work+0x72a and nothing corrects it.
    /// </summary>
    public const nint ChSeqFrame = 0x4377C0;

    /// <summary>
    ///     The motion cross-fade, FUN_00839630(work, hokan). Blends each channel by
    ///     (target - current) / hokan; the advance counts hokan down immediately afterwards.
    /// </summary>
    public const nint ChMotionInterpolate = 0x4395B0;

    /// <summary>
    ///     The idle look-around, FUN_00834570(actor). Cdecl with one argument, measured: the body
    ///     ends in a plain c3 and its only call site, inside the per-actor worker FUN_008335b0,
    ///     pushes edi and clears eight bytes for this call and the aim blend together.
    ///
    ///     The argument is the actor pointer despite the decompiler typing it float: the entry loads
    ///     [ebp+8] into edi and tests the byte at edi+0x4d4, the flag that says whether this actor
    ///     looks around at all. Everything the function advances is counted in calls, and no branch
    ///     in it reads sg_rate.
    /// </summary>
    public const nint ChIdleSway = 0x4344F0;

    /// <summary>
    ///     Ch_NeckCalc(actor), the head and neck tracking. Cdecl with one argument, measured: both
    ///     of its returns are a plain c3 and its single call site, in the per-actor worker
    ///     FUN_008335b0 immediately after the idle look-around, pushes edi and clears eight bytes
    ///     for the two calls together.
    ///
    ///     Unlike the look-around this one does load the stack cookie - a1 d8 13 c6 00 / 33 c5 at
    ///     the entry - so its frame is checked on the way out. That constrains nothing a detour
    ///     does, but it does mean the prologue is longer than the look-around's.
    ///
    ///     Three clocks advance once per call and none of them reads sg_rate: the weight ramp on
    ///     0x418, the yaw blend on 0x41c, and the slerp fade on 0x4bc. The rates of the last two
    ///     are shared .rdata constants with dozens of other readers, so they are not patchable.
    /// </summary>
    public const nint ChNeckCalc = 0x4348D0;

    /// <summary>
    ///     graphicTextureVideoUpdate. The texture video path, which is not the FMV path and is not
    ///     reached by the FMV frameskip.
    /// </summary>
    public const nint GraphicTextureVideoUpdate = 0x2442C0;

    /// <summary>graphicDrawMainMenuWaterEffect. One scrolling image per frame.</summary>
    public const nint GraphicDrawMainMenuWaterEffect = 0x23E920;

    /// <summary>
    ///     graphicVideoUpdate. Stdcall, no arguments. The video update the FMV path runs per frame,
    ///     and the one the engine's own developers frameskip to get correct playback speed out of a
    ///     29.97 source. Preferred over PhyFMVPlayerManager::UpdateTexture (0x2D77B0), which sits
    ///     deeper in the manager and cannot be skipped without leaving it mid-state.
    /// </summary>
    public const nint GraphicVideoUpdate = 0x245DF0;

    /// <summary>
    ///     sFMVPlayerManager, the PhyFMVPlayerManager singleton pointer. Byte 0x6D0 of the instance
    ///     is the playback flag graphicVideoUpdate tests before it does anything: while it is clear
    ///     the function returns immediately, so a call is not a video frame.
    /// </summary>
    public const nint FmvPlayerManager = 0x8DED3C;

    /// <summary>
    ///     PhyFMVPlayerManager's playback entry, thiscall with one stack argument. It compares the
    ///     path buffer at instance+0x4F0 against the empty string, and when that is non-empty calls
    ///     fiosUnifyFilename on it, traces "start to play video:%s" and hands it to the player. The
    ///     buffer is the path in full, prefix included, and it is the manager's own memory rather
    ///     than a literal - so it can be rewritten in place here, once per video, instead of
    ///     answering for every file the engine opens.
    /// </summary>
    public const nint FmvPlayStart = 0x2D7430;

    /// <summary>
    ///     Offset of that path buffer inside the manager. The trace in the engine's own log is what
    ///     it holds: ../../../FFX_Data/GameData/PS3Data/Video/OPL_us.webm, authored case intact.
    /// </summary>
    public const int FmvPathOffset = 0x4F0;

    // --- Globals (RVA, same convention as the hook targets) ---

    /// <summary>
    ///     short. Filter slot 0's remaining step count, and the only field of the slot whose reaching
    ///     zero ends the cross-fade. Sg_AccSetAlpha hands its address to PostProcessManager, which
    ///     keeps the pointer and dereferences it on every rendered frame, so writing it directly is
    ///     seen without going back through the setter.
    /// </summary>
    public const nint CrossFadeSlot0Counter = 0xF0070C;

    /// <summary>
    ///     short. Filter slot 0's current alpha, 0 to 0x80. On the HD build this is the motion blur
    ///     weight: the render pass divides it by 128 and hands it to MotionBlur::setMotionBlurWeight.
    ///     Restoring it after the engine has cleared it is what would leave a permanent trail.
    /// </summary>
    public const nint CrossFadeSlot0Alpha = 0xF0070E;

    /// <summary>uint. Target framerate is 60 / this. The engine uses 1 in menus and 2 elsewhere.</summary>
    public const nint SFlipVSyncInterval = 0x830E88;

    /// <summary>uint. Horizontal blank counter; the engine's own frame counter.</summary>
    public const nint SgCount = 0x1FCBBF0;

    /// <summary>
    ///     Slot 764 of gMagicFunctions (base 0x00C64CE8, so base + 0xBF0), the cell that hands
    ///     &amp;sg_count to every magic overlay. It holds the address, not the value.
    ///
    ///     MagicFile_Start passes the table base to the DLL's InitMagicPRX, which is byte-identical
    ///     boilerplate in all 581 shipped overlays and copies 32 data slots out of it into DLL-local
    ///     cells at load time. Twenty overlays then read this one, three sites each, always as
    ///     <c>test byte ptr [eax], 3</c> or <c>, 7</c> around a particle emission.
    ///
    ///     Writable without disturbing anything else: the cell has no reference anywhere in FFX.exe
    ///     (0 decoded, 0 raw dword occurrences in the whole image) and the table itself has exactly
    ///     one, the <c>push 0xC64CE8</c> at 0x009DA7FF. FFX.exe's own sg_count readers all address
    ///     the global directly. The slot carries a base relocation, so at runtime it holds
    ///     module base + <see cref="SgCount"/> rather than the linked 0x023CBBF0.
    /// </summary>
    public const nint MagicFunctionsSgCountSlot = 0x8658D8;

    /// <summary>
    ///     int. The number of Sg_MainLoop passes updateFFX still owes this frame: it repeats its
    ///     whole body while this is non-zero, and Sg_MainLoop consumes one pass per iteration.
    ///
    ///     FUN_00821f90 is the only writer on the ordinary path and it clamps the value to 1 unless
    ///     two conditions both hold - the accumulated wall clock delta has reached 2/60 s, and
    ///     FUN_0081fe40 (a per-scene gate that refuses in battle, during a video and under a movie
    ///     camera) returns non-zero. Above that threshold it becomes floor(elapsed seconds * 30),
    ///     the number of 30 Hz steps needed to catch up, and the passes beyond the first run with
    ///     gParticleDoNotRender and gFrameSkipDoNotRender set, so they simulate without drawing.
    ///     During a syncdata catch-up it is instead incremented once per recorded PS2 frame consumed.
    /// </summary>
    public const nint ElapsedFrameCountFrameSkip = 0xEFB808;

    /// <summary>
    ///     uint. Vertical blank counter. Sg_MainCalcRate derives the animation rate from its delta:
    ///     sg_rate = (sg_vcount - previous) * 0x80, and sg_ratef = sg_rate / 256, so a delta of 2 is
    ///     the 1.0 that means "one full 30 Hz step".
    /// </summary>
    public const nint SgVCount = 0xEFB7F0;

    /// <summary>uint. Second vertical blank counter, advanced in lockstep with the first.</summary>
    public const nint SgVCount2 = 0xEFB7F4;

    /// <summary>int. Animation rate in 1/256 units; 0x100 is full speed.</summary>
    public const nint SgRate = 0x1FCBBEC;

    /// <summary>float. sg_rate as a factor; 1.0 is full speed.</summary>
    public const nint SgRateF = 0x1FCBBE8;

    /// <summary>
    ///     int. The engine's own count of live particle objects, accumulated during the dispatcher
    ///     pass and reset each frame - so it has to be read from inside a frame to mean anything.
    /// </summary>
    public const nint PobjCounter = 0x1F0FD24;

    /// <summary>uint. Particle stop request.</summary>
    public const nint PpvUserStopPartF = 0x1F0FD34;

    /// <summary>
    ///     int. The global particle time step, in the same fixed-point units as each manager's
    ///     accumulator. Zero in the image and written at runtime; observed as 0x1000.
    /// </summary>
    public const nint PpvPartTimeStep = 0x94B7EC;

    /// <summary>int. Number of active particle managers.</summary>
    public const nint PpvPartManagerCount = 0x94B4C8;

    /// <summary>Particle manager array. Stride 0x80; the time step is at +0x10 of each entry.</summary>
    public const nint PpvPartManagers = 0x94E390;

    /// <summary>
    ///     int. Absolute pointer to the field particle group table, zero until field data is loaded.
    ///     The group count is the ushort at its +4; the per-group records start at its +0x20 with a
    ///     stride of 0x50.
    ///
    ///     Any reader has to test it for zero. pppFpLoop itself does not: it dereferences the count
    ///     directly and relies on g_FpLoopFlag, which pppSetFpPdt sets in the same call that fills
    ///     this in. Its neighbours, which have no such outer gate, all test it.
    /// </summary>
    public const nint PpvFpGroupTable = 0x92C258;

    /// <summary>
    ///     Field particle manager array, one 0x80-byte entry per group in <see cref="PpvFpGroupTable"/>.
    ///     +0x00 is the restart countdown, 0xFFFFF000 while the group is dead; +0x10 the time step;
    ///     +0x1C and +0x1D the two flags _pppRunPartFp reads to decide how the group ends.
    /// </summary>
    public const nint PpvFpManagers = 0x92C260;

    /// <summary>
    ///     The <c>test byte ptr [sg_count], 1</c> in __TODrawWaitBtlWinPrepare (0x008A6810) that
    ///     gates the multi-target cursor draw, so its immediate is the blink period. Verified in the
    ///     shipped image as F6 05 F0 BB 3C 02 01, followed by the jump that skips the draw loop.
    ///     Patched, not hooked: the function also stamps the window's elapsed time and draws the
    ///     command window.
    /// </summary>
    public const nint BattleCursorBlinkGate = 0x4A68EB;

    /// <summary>
    ///     The <c>cmp ax, 3</c> in graphicDrawDream (0x0063E120) that decides how often the overlay's
    ///     star field is redrawn, so its immediate is the star pass period in presented frames.
    ///     Verified in the shipped image as 66 83 F8 03, preceded by the inc and the store of the
    ///     counter at 0x00CCB46C and followed by the branch that skips the reset.
    /// </summary>
    public const nint DreamStarCounterCompare = 0x23E2D3;

    public const nint DreamStarCounter = 0x8CB46C;

    /// <summary>uint. Non-zero while an FMV is playing.</summary>
    public const nint GMoviePlay = 0xD2A008;

    /// <summary>byte, 0..69. Current frame of the main menu water animation.</summary>
    public const nint MenuWaterFrame = 0x8CBA09;

    /// <summary>double in .rdata. Hardcoded 29.97 target framerate of the video update loop's time step.</summary>
    public const nint TextureVideoTargetFramerate = 0x74A178;

    /// <summary>uint. Limit timer mode; the timer only runs when this equals 2.</summary>
    public const nint LimitTimerMode = 0xF3F77C;

    /// <summary>float. Accumulated limit timer frames.</summary>
    public const nint LimitTimerFrames = 0xF3F788;

    /// <summary>float. Limit timer base duration in seconds.</summary>
    public const nint LimitTimerBase = 0xF3F78C;

    /// <summary>float. Raw remaining limit time.</summary>
    public const nint LimitTimerRaw = 0xF3F790;

    /// <summary>float. Displayed limit time, deliberately jittered in its last digit.</summary>
    public const nint LimitTimerRounded = 0xF3F794;

    /// <summary>
    ///     uint. EnableGameTextureAnimation. The image ships it as 1; FUN_00836790 is the only
    ///     writer and FUN_006bb810 calls it with 0 on one branch. Zero disables the whole character
    ///     texture animation advance.
    /// </summary>
    public const nint EnableGameTextureAnimation = 0x849720;

    /// <summary>
    ///     uint. g_isNeedSync. While it is 1, Sg_MainCalcRate takes sg_rate from the syncdata table
    ///     instead of from the vertical blank delta, which discards this module's correction.
    /// </summary>
    public const nint IsNeedSync = 0xEFB8A0;

    /// <summary>
    ///     uint. force_wait_blur_frame_count. Set to 0x5a by the eternal effect VM's opcode 0x0c
    ///     handler at 0x007f91a0 and counted down once per MsBtlReadManage call, which is once per
    ///     presented frame. Battle state 0x16 waits for it to reach zero before the encounter starts.
    /// </summary>
    public const nint ForceWaitBlurFrameCount = 0xD2CA64;

    /// <summary>
    ///     The eternal effect VM's transition blur, opcode 0x0c of the object opcode table. It is
    ///     what writes the 90-frame battle entry hold.
    /// </summary>
    public const nint EternalBlurTransition = 0x3F9280;

    // --- Texture animation candidates, survey only ---

    /// <summary>chr_texanim_draw. Dispatches to the old or new per-character texture animation draw.</summary>
    public const nint ChrTexAnimDraw = 0x37C830;

    /// <summary>screenTextureAnimationDraw.</summary>
    public const nint ScreenTextureAnimationDraw = 0x50BDB0;

    /// <summary>setMaterialUVScroll. Scrolling UVs rather than a frame sequence.</summary>
    public const nint SetMaterialUVScroll = 0x2AC870;

    /// <summary>Ch_TextureAnimSetEnable.</summary>
    public const nint ChTextureAnimSetEnable = 0x430D10;

    /// <summary>
    ///     Ch_TextureSetAnimTimer(chr, timer). Cdecl, measured: the function ends in a plain c3 and
    ///     cleans nothing. It forwards to tex_anim_timer when chr+0x6FC is set, which stores the low
    ///     byte at tex_anim_wk + slot * 0x30 + 0xE.
    ///
    ///     That byte is a step, not a period: FUN_0077f450 adds it to a sprite sequence accumulator
    ///     and subtracts it from a blink countdown, once per call in either case, and the duration it
    ///     is measured against lives in the asset. So it must not be scaled. The two engine writers
    ///     say the same thing - MsCalcMotionSpeed writes only the literals 1 and 0 on a motion speed
    ///     zero crossing, which is a pause and a resume rather than a rate. The other callers are
    ///     MsBtlBridgeBtl2Event and one magic-side function.
    ///
    ///     The module hooks it as a pass-through, so the value reaches the engine unchanged. The hook
    ///     is kept because it is the only place the writes are visible; the correction the byte can
    ///     express is the per-slot hold in <see cref="VrrConfig.TextureAnimationStep"/>.
    /// </summary>
    public const nint ChTextureSetAnimTimer = 0x43D070;

    // --- Eternal Calm copyright card ---

    /// <summary>
    ///     void ToDrawEternalCalmCopyRight(int frames). Cdecl with one argument, measured: the
    ///     function ends in a plain c3 and the only call site cleans with add esp,4.
    ///
    ///     The function catalog says it takes no argument, and that is wrong. Its one call site in
    ///     Sg_MainLoop reads the counter into eax, increments it, pushes eax, and only then stores
    ///     eax to the global - so the argument is a register push with no data reference behind it,
    ///     and the call-site argument counter never saw one. The decompiled body recovers the
    ///     parameter and reads it at [ebp+8], which is the disagreement that gives it away.
    ///
    ///     The argument is a phase clock for the card's alpha: below 0x20 the alpha ramps up, below
    ///     0x110 it holds at 0x80, above that it ramps down. The step itself is per call, not per
    ///     unit of the argument.
    /// </summary>
    public const nint ToDrawEternalCalmCopyRight = 0x504A70;

    /// <summary>
    ///     int. The Eternal Calm card's frame counter. Sg_MainLoop is its only writer and holds its
    ///     only two readers: the increment that feeds the call, and the compare against 0x130 that
    ///     sets IsEternalCalmOver to 2 once the card has been up for 304 frames.
    /// </summary>
    public const nint EternalCalmCardFrames = 0xEFB7C0;

    /// <summary>
    ///     int. The Eternal Calm card's alpha, 0 to 0x80. ToDrawEternalCalmCopyRight is the only
    ///     function that reads or writes it, and it is the only state the call leaves behind.
    /// </summary>
    public const nint EternalCalmCardAlpha = 0x14716A4;
    /// <summary>Initializes per-level ATEL movement and rotation defaults.</summary>
    public const nint AtelInitMotionRecords = 0x46D050;
    public const nint FmvInitialize = 0x2D9C80;
    public const nint FmvGetVideoName = 0x2D93E0;
}
