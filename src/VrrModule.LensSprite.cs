namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The sprite frame clock of the lens and flare family, which is the one place in the particle
///     system where a rate can be halved exactly instead of a call being skipped.
///
///     KeLnsShp_Update advances an accumulator by a fixed 0x200 per call and compares it against the
///     current sprite frame's own duration, carrying the remainder across frames. Its four callers
///     are the four Lns draw kernels, so it runs once per drawn frame and doubles at 60 Hz - this is
///     the rhythm of a flare or a background flash, and no hold anywhere else reaches it.
///
///     The four Lns*Update kernels do not: their entire body is the keyframe one-shot that resolves
///     the shape pointer and seeds this accumulator. Holding them retimes nothing and costs the
///     keyframe, which is why they stay excluded.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>What the engine adds per call, and therefore what has to be given back.</summary>
    private const short LensSpriteStep = 0x200;

    private long _lens_sprite_calls;

    private string lens_sprite_counts() => $"lens_clock={_lens_sprite_calls}";

    private bool init_lens_sprite_hook()
    {
        if (!_config.LensSpriteClock) return true;

        return hook_or_log(HookSiteId.LensSpriteKeLnsShpUpdate, "KeLnsShp_Update", EngineAddresses.KeLnsShpUpdate,
            () => new FhCallHandle<d_lens_shape_update>(new FhCallLocation(EngineAddresses.KeLnsShpUpdate, 0))
                .hook(this, h_lens_shape_update));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_lens_shape_update(nint work);

    /* Gives back part of the step before the original adds it, which turns +0x200 per call into
     * +0x200/Scale on simulation calls. Frozen calls skip this clock-only leaf; the frame index,
     * remainder carry, loop and hold-last handling remain native.
     *
     * This is a scale rather than a skip, so no sprite frame is dropped however short its duration.
     * The accumulator is the first field of the block param_1[1] points at, and the pointer
     * indirection is the engine's own - get_ptr32_func is the identity on PC. */
    private static void prepare_lens_sprite(nint work)
    {
        if (work == 0) return;
        short* accumulator = *(short**)(work + 4);
        if (accumulator == null) return;

        float step = LensSpriteStep / Math.Max(1f, Scale);
        *accumulator -= (short)(LensSpriteStep - step);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_lens_shape_update(nint work)
    {
        _lens_sprite_calls++;

        // Cancelling the increment still consumes duration debt; this leaf has no geometry to retain.
        if (RenderPassContext.IsPresentation || FhUtil.get_at<int>(EngineAddresses.PpvUserStopPartF) != 0) return;

        prepare_lens_sprite(work);

        new FhCallHandle<d_lens_shape_update>(new FhCallLocation(EngineAddresses.KeLnsShpUpdate, 0))
            .chain_from(h_lens_shape_update).fnptr?.Invoke(work);
    }
}
