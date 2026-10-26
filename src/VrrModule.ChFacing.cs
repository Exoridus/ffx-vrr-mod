namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     How fast a character turns to face where it is going. This is the largest per-call rate left
///     in the Ch subsystem and it applies to every actor in the game that walks, swims or stands
///     still: the two loop-1 motion controllers step the facing at <c>Chr+0x158</c> towards its
///     target at <c>Chr+0x168</c> by a constant number of radians per call, selected from three by
///     the actor's speed, and neither controller reads the delta the rest of loop 1 is handed.
///
///     <list type="bullet">
///     <item>standing, speed exactly zero: 0.31415921 rad, 18 degrees per call;</item>
///     <item>walking, speed at or below the threshold at <c>Chr+0x170</c>: 0.34906578, 20 degrees;</item>
///     <item>running, above it: 0.52359867, 30 degrees.</item>
///     </list>
///
///     At 30 Hz that is 540, 600 and 900 degrees per second. At 60 Hz it is all doubled, so a
///     character spins to its new heading in half the authored time while its translation, which the
///     corrected delta does reach, still takes the authored time. The visible result is an actor that
///     snaps round and then walks off, rather than turning into its walk.
///
///     The correction divides the three constants and nothing else. It is not a hold: holding the
///     facing would freeze turning on every second frame while translation kept running, which is
///     the reason the buoyancy hold deliberately leaves the same field alone.
///
///     <para>Patched at the six instruction operands rather than at the function or the data.</para>
///     The constants are not private to these two controllers. Each is an <c>fld dword ptr [abs32]</c>
///     out of a shared literal pool, and 0.34906578 has a third reader at <c>0x0082D70E</c> that has
///     nothing to do with actor facing - so writing the pool would retime that too. The alternative,
///     a detour on the step helper Sg_StepAngleTowards that the constant is an argument to, returns
///     its result in st(0) and has a third caller of its own in Ch_CalcElement, so it would need a
///     float-returning delegate and would also change a quantity nobody has looked at. Rewriting the
///     six operands to point at three floats this module owns changes exactly the six sites, keeps
///     the engine's own instruction stream, and lets the values follow a rate that moves.
///
///     Nothing in the verification is an absolute address, which is the lesson of the battle cursor
///     patch: each site is checked by its two-byte opcode, by the register-relative compare and
///     branch in front of it, and by the value the operand currently points at, read through the
///     pointer as the loader left it.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     One of the three turn rates, and the register-relative bytes that identify the site.
    ///
    ///     <paramref name="Prefix"/> ends immediately before the instruction, so it is checked at
    ///     <c>Rva - Prefix.Length</c>: the speed compare and its branch for the two speed-dependent
    ///     sites, and the short jump over the walking case for the standing one.
    /// </summary>
    private readonly record struct ChFacingSite(string Name, nint Rva, byte[] Prefix, int Slot, float Vanilla);

    /// <summary><c>D9 05</c>, <c>fld dword ptr [abs32]</c>. The operand is the four bytes after it.</summary>
    private static ReadOnlySpan<byte> ChFacingLoadOpcode => [0xD9, 0x05];

    private const int ChFacingOperandOffset = 2;

    /* test ah,5 / jp - the "speed above the walk threshold" arm, which selects the running rate.
     * The fstp between them discards the loser of the compare. */
    private static readonly byte[] ChFacingRunningPrefix  = [0xF6, 0xC4, 0x05, 0x7A, 0x0A, 0xDD, 0xD8];

    /* test ah,0x44 / jnp - "speed is exactly zero", whose not-taken arm is the walking rate. */
    private static readonly byte[] ChFacingWalkingPrefix  = [0xF6, 0xC4, 0x44, 0x7B, 0x08];

    /* The jump the walking arm ends with, which is all that separates the two loads. */
    private static readonly byte[] ChFacingStandingPrefix = [0xEB, 0x06];

    private const float ChFacingRunning  = 0.52359867f;
    private const float ChFacingWalking  = 0.34906578f;
    private const float ChFacingStanding = 0.31415921f;

    /// <summary>
    ///     The six sites, three per controller: the walk controller at <c>0x435C30</c> and the swim
    ///     controller at <c>0x435DB0</c> carry the same three-way selection with the same three
    ///     constants, and both are called from Ch_CalcMain's first per-actor loop.
    /// </summary>
    private static readonly ChFacingSite[] ChFacingSites =
    [
        new("walk controller, running",  EngineAddresses.ChWalkMotionController + 0x2E, ChFacingRunningPrefix,  0, ChFacingRunning),
        new("walk controller, walking",  EngineAddresses.ChWalkMotionController + 0x45, ChFacingWalkingPrefix,  1, ChFacingWalking),
        new("walk controller, standing", EngineAddresses.ChWalkMotionController + 0x4D, ChFacingStandingPrefix, 2, ChFacingStanding),
        new("swim controller, running",  EngineAddresses.ChSwimMotionController + 0x2C, ChFacingRunningPrefix,  0, ChFacingRunning),
        new("swim controller, walking",  EngineAddresses.ChSwimMotionController + 0x43, ChFacingWalkingPrefix,  1, ChFacingWalking),
        new("swim controller, standing", EngineAddresses.ChSwimMotionController + 0x4B, ChFacingStandingPrefix, 2, ChFacingStanding),
    ];

    /// <summary>
    ///     The three floats the rewritten operands point at, in slot order: running, walking,
    ///     standing. Allocated once for the life of the process, because the instructions that read
    ///     it outlive any managed object graph and the journal restores the operands rather than
    ///     this block.
    /// </summary>
    private float* _ch_facing_rates;

    private int  _ch_facing_patched;
    private float _ch_facing_scale;

    private string ch_facing_counts()
        => $"chfacing={_ch_facing_patched}/{_ch_facing_scale:F2}";

    /// <summary>
    ///     Verifies and rewrites the six operands once. The values themselves are written every
    ///     frame by <see cref="patch_ch_facing_rates"/>, which runs after this and is what makes the
    ///     correction follow a measured rate.
    /// </summary>
    private bool init_ch_facing_patch()
    {
        if (!_config.ChFacingTurnRate) return true;

        _ch_facing_rates = (float*)NativeMemory.Alloc(3, sizeof(float));

        // Seeded with the vanilla rates before any operand points here, so that a frame presented
        // between this and the first rate patch reads the engine's own numbers rather than zeros.
        _ch_facing_rates[0] = ChFacingRunning;
        _ch_facing_rates[1] = ChFacingWalking;
        _ch_facing_rates[2] = ChFacingStanding;

        // Every site is verified before any is written. Three of the six belong to one controller
        // and three to the other, and a controller with two of its three rates redirected would
        // turn at one rate standing and another walking - a partial application is worse than none
        // and harder to recognise than a refusal.
        foreach (ChFacingSite site in ChFacingSites)
        {
            byte* at = FhUtil.ptr_at<byte>(site.Rva);

            if (!new ReadOnlySpan<byte>(at, ChFacingLoadOpcode.Length).SequenceEqual(ChFacingLoadOpcode)
             || !new ReadOnlySpan<byte>(at - site.Prefix.Length, site.Prefix.Length).SequenceEqual(site.Prefix))
            {
                _logger.Error($"[Vrr] Ch facing site {site.Name} at RVA 0x{site.Rva:X} does not carry " +
                              $"fld dword ptr [abs32] behind its expected compare; no site written.");
                return false;
            }

            // The operand is relocated, so the check on it is the value it points at, not the
            // pointer. A site whose constant is not the one this module divides is a site that has
            // moved, and rewriting it would retime something unexamined.
            float* current = *(float**)(at + ChFacingOperandOffset);

            if (current == null || MathF.Abs(*current - site.Vanilla) > 1e-5f)
            {
                _logger.Error($"[Vrr] Ch facing site {site.Name} at RVA 0x{site.Rva:X} points at " +
                              $"{(current == null ? "null" : (*current).ToString("F8"))} rather than " +
                              $"{site.Vanilla:F8}; no site written.");
                return false;
            }
        }

        foreach (ChFacingSite site in ChFacingSites)
        {
            _patches.Write(site.Rva + ChFacingOperandOffset,
                           BitConverter.GetBytes((uint)(nint)(_ch_facing_rates + site.Slot)));
            _ch_facing_patched++;
        }

        _logger.Info($"[Vrr] Ch facing turn rates redirected at {_ch_facing_patched} sites.");
        return true;
    }

    /// <summary>
    ///     Called once per presented frame from <c>apply_rate_patches</c>. Three divisions and a
    ///     compare; it writes nothing while the rate has not moved.
    ///
    ///     No rounding and no clamp: these are floats the engine only ever compares against an angle
    ///     difference, so a rate of any magnitude is well defined, and a scale of one restores the
    ///     constants exactly because the division is by 1.
    /// </summary>
    private void patch_ch_facing_rates()
    {
        if (_ch_facing_rates == null) return;

        float scale = Scale;
        if (scale == _ch_facing_scale) return;

        _ch_facing_scale   = scale;
        _ch_facing_rates[0] = ChFacingRunning  / scale;
        _ch_facing_rates[1] = ChFacingWalking  / scale;
        _ch_facing_rates[2] = ChFacingStanding / scale;
    }

    // --- The scripted element pitch, the same class one field along ---

    private long _ch_pitch_sets;
    private long _ch_pitch_scaled;

    /// <summary>
    ///     Pitch arms seen and pitch arms whose rate was divided. The two differ by the snaps, which
    ///     are half of the sixty authored call sites.
    /// </summary>
    private string ch_pitch_counts() => $"chpitch={_ch_pitch_sets}/{_ch_pitch_scaled}";

    /// <summary>
    ///     ATEL <c>?setPitch [506Dh]</c> aims an actor's element pitch at an angle and gives it a
    ///     rate to get there, and that rate is degrees per call.
    ///
    ///     The setter stores the target at <c>Chr+0x160</c> and the rate at <c>Chr+0x164</c>
    ///     converted to radians, and Ch_CalcElement steps <c>Chr+0x15c</c> towards the target by at
    ///     most that much once per presented frame through the same Sg_StepAngleTowards the facing
    ///     uses, then applies the result as an X rotation on the element matrix. So it is the facing
    ///     correction one field along, with the rate coming from a script argument instead of from
    ///     one of three constants.
    ///
    ///     Corrected at the setter rather than at the step, for the reason the twelve ATEL rate
    ///     setters are: it is a leaf with one caller, the opcode handler, and the field it writes is
    ///     the only place the value lives.
    ///
    ///     <para>A rate of zero is not a slow turn.</para> The setter branches on it: zero assigns
    ///     the target to the current angle as well and stores a rate of zero, which is the snap, and
    ///     the step then moves nothing. Half of the sixty authored sites pass zero, so scaling it
    ///     would turn thirty hard cuts into thirty one-frame ramps.
    /// </summary>
    private bool init_ch_pitch_hook()
    {
        if (!_config.ChPitchTurnRate) return true;

        return hook_or_log(HookSiteId.ChFacingChSetPitchTarget, "Ch_SetPitchTarget", EngineAddresses.ChSetPitchTarget,
            () => new FhCallHandle<d_ch_set_pitch>(new FhCallLocation(EngineAddresses.ChSetPitchTarget, 0))
                .hook(this, h_ch_set_pitch));
    }

    /* Measured cdecl: the body reads [ebp+8], [ebp+0xc] and [ebp+0x10] and ends 5d c3, so the caller
     * cleans. Both angle and rate are floats - the setter loads them with fld dword and converts the
     * rate with an fmul and an fdiv against pi and 180, which are 8-byte doubles in .rdata and are
     * shared, so neither is a lever. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ch_set_pitch(nint actor, float angle, float rate);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ch_set_pitch(nint actor, float angle, float rate)
    {
        _ch_pitch_sets++;

        float scale = Scale;

        if (scale != 1f && rate != 0f)
        {
            rate /= scale;
            _ch_pitch_scaled++;
        }

        new FhCallHandle<d_ch_set_pitch>(new FhCallLocation(EngineAddresses.ChSetPitchTarget, 0))
            .chain_from(h_ch_set_pitch).fnptr!(actor, angle, rate);
    }
}
