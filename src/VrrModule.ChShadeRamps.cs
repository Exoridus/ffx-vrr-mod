namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Every character fade, tint and transparency ramp. Each actor carries seven
///     <c>{current, target, frames_remaining}</c> records at <c>Chr+0x330</c>, stride <c>0xc</c>, and
///     Ch_CalcMain steps all seven for every live actor once per presented frame:
///
///     <code>
///     if (target != current) current += (target - current) / frames_remaining;
///     if (frames_remaining != 0) frames_remaining--;
///     </code>
///
///     There is no time term anywhere in it, so at 60 Hz every ramp finishes in half its authored
///     wall clock. The seven are the three shade colour channels (Ch_SetShadeCol), the transparency
///     (Ch_SetTransparent), the shade amount (Ch_SetShade), the shade coefficient (Ch_SetShadeID)
///     and the specular alpha (Ch_SetSpecularAlpha) - which is to say every character fade-in,
///     fade-out, blackout, tint and dissolve in the game. One event, <c>znkd0600</c>, arms
///     <c>SgEvent.setActorLight [4013h]</c> 36 times.
///
///     <para>Corrected at the arm, which is the one place it is exact.</para> All seven setters
///     funnel into a single three-line helper that writes the target and the count, so multiplying
///     the count there is a change of one number in one function. It is exact rather than
///     approximate because the step divides by the count that is left rather than by the count it
///     started with: a ramp armed for <c>2N</c> frames advances <c>(target-current)/2N</c> on its
///     first call and reaches the target on exactly its <c>2N</c>th, so doubling the count doubles
///     the duration and changes nothing else. No accumulator, no carry, no drift, and no per-frame
///     work at all.
///
///     A hold on the step would have been the obvious shape and is worse here: the step runs seven
///     times per actor per frame off one loop, so a hold needs either a phase per record or a global
///     parity that misplaces by a frame every ramp armed on a held frame.
///
///     <para>A count of zero is passed through untouched.</para> Zero is not a short ramp, it is the
///     helper's snap: the engine assigns the target to the current value and the step never touches
///     the record again. Scaling it would turn every instant assignment into a one-frame ramp.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private long _ch_ramp_arms;
    private long _ch_ramp_scaled;

    /// <summary>
    ///     Arms seen and arms whose count was stretched. The two differ by the instant assignments
    ///     and by whatever is armed while the engine is pacing itself from the syncdata table, so a
    ///     gap between them is expected rather than a fault.
    /// </summary>
    private string ch_ramp_counts() => $"chramp={_ch_ramp_arms}/{_ch_ramp_scaled}";

    private bool init_ch_shade_ramp_hook()
    {
        if (!_config.ChShadeRamps) return true;

        return hook_or_log(HookSiteId.ChShadeRampsChSetRampTarget, "Ch_SetRampTarget", EngineAddresses.ChSetRampTarget,
            () => new FhCallHandle<d_ch_set_ramp>(new FhCallLocation(EngineAddresses.ChSetRampTarget, 0))
                .hook(this, with_hook_continuation<d_ch_set_ramp>(h_ch_set_ramp)));
    }

    /* Measured cdecl: the function ends 5d c3 on both of its two returns, so the caller cleans.
     * The count is an int and not a float - the body does mov eax,[ebp+0x10] / test eax,eax / dec
     * eax on it, and only the target is touched by the FPU. Ghidra types the record float* and
     * therefore prints the count as a float, which it is not. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_ch_set_ramp(nint record, float target, int frames);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_ch_set_ramp(nint record, float target, int frames)
    {
        if (!RuntimeEffectsArmed)
        {
            HookSites.Native(() => new FhCallHandle<d_ch_set_ramp>(new FhCallLocation(EngineAddresses.ChSetRampTarget, 0)).chain_from(h_ch_set_ramp).fnptr)(record, target, frames);
            return;
        }

        _ch_ramp_arms++;

        float scale = Scale;
        int   armed = frames;

        // Only a positive count is a duration. Zero is the snap, and a negative count would step
        // away from the target for ever in either version, so neither is this module's business.
        if (scale != 1f && frames > 0)
        {
            armed = (int)Math.Clamp(Math.Round(frames * (double)scale), 1, int.MaxValue);
            _ch_ramp_scaled++;
        }

        new FhCallHandle<d_ch_set_ramp>(new FhCallLocation(EngineAddresses.ChSetRampTarget, 0))
            .chain_from(h_ch_set_ramp).fnptr!(record, target, armed);
    }
}
