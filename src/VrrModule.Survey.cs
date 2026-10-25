namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Call counters on the candidates for the two systems that still run at double speed. They
///     change no behaviour; they answer which function is actually on the live path, which name
///     matching alone has already got wrong twice in this module's history.
///
///     Read the counts from the telemetry line while the effect in question is on screen: an
///     animated weapon texture, or a campfire.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private long _texanim_draw;
    private long  _screen_texanim_draw;
    private long  _screen_texanim_changes;

    /// <summary>
    ///     Last animation id per drawn slot, not one global last id.
    ///
    ///     The global version counted a change on almost every call and that meant nothing: the
    ///     stadium draws about four screen animations per frame, so consecutive calls name different
    ///     animations and a single shared "last id" flips every time. Keyed per slot the counter
    ///     answers the question it was built for - how often one animation advances - and the
    ///     interesting ratio is changes against calls per slot rather than in total.
    /// </summary>
    private readonly Dictionary<(int Table, int Bank, int Index), ulong> _screen_texanim_ids = [];
    private long _uv_scroll;
    private long _texanim_set_enable;
    /// <summary>
    ///     Calls that reached the old format's advance. Counted in the hold that hooks it rather than
    ///     at the dispatcher: the dispatcher is declared with one parameter and its single caller
    ///     passes three, so a one-argument chain leaves the original reading two garbage stack slots.
    ///     That faulted twice, both times on the fourth boot splash.
    /// </summary>
    private long _texanim_advance_old_path;

    private bool init_survey_hooks()
    {
        if (!_config.SurveyTextureAnimation) return true;

        // Failures are not fatal here: a probe that will not attach is itself a result. But that
        // result has to be visible - a count of zero used to be indistinguishable from a probe that
        // never installed, and one of them was exactly that. The install outcome is logged per
        // probe, and a chain that does not resolve leaves fnptr null, so a counting probe can never
        // be the thing that ends the process.
        probe("chr_texanim_draw", EngineAddresses.ChrTexAnimDraw,
            () => new FhCallHandle<d_texanim_draw>(new FhCallLocation(EngineAddresses.ChrTexAnimDraw, 0)).hook(this, h_texanim_draw));
        probe("screenTextureAnimationDraw", EngineAddresses.ScreenTextureAnimationDraw,
            () => new FhCallHandle<d_screen_texanim_draw>(new FhCallLocation(EngineAddresses.ScreenTextureAnimationDraw, 0)).hook(this, h_screen_texanim_draw));
        probe("setMaterialUVScroll", EngineAddresses.SetMaterialUVScroll,
            () => new FhCallHandle<d_uv_scroll>(new FhCallLocation(EngineAddresses.SetMaterialUVScroll, 0)).hook(this, h_uv_scroll));
        probe("Ch_TextureAnimSetEnable", EngineAddresses.ChTextureAnimSetEnable,
            () => new FhCallHandle<d_texanim_set_enable>(new FhCallLocation(EngineAddresses.ChTextureAnimSetEnable, 0)).hook(this, h_texanim_set_enable));

        _logger.Info("[Vrr] Texture animation survey armed; counts appear in the telemetry line.");
        return true;
    }

    private void probe(string name, nint rva, Func<bool> install)
        => _logger.Info(install()
            ? $"[Vrr] Probe attached: {name} at RVA 0x{rva:X}."
            : $"[Vrr] Probe did NOT attach: {name} at RVA 0x{rva:X}; its count stays 0 for that reason.");

    private string survey_counts()
        => $"texanim_draw={_texanim_draw} screen_texanim={_screen_texanim_draw} " +
           $"sta_changes={_screen_texanim_changes} sta_slots={_screen_texanim_ids.Count} " +
           $"uv_scroll={_uv_scroll} texanim_enable={_texanim_set_enable} " +
           $"advance_old={_texanim_advance_old_path}";

    /* Cdecl, read off the bytes. The function is a dispatcher: it branches on the format byte in
     * tex_anim_wk and either returns through a plain ret at +0x28 or tail-jumps into
     * chr_texanim_draw_sub_old/_new. That ret is the evidence - the caller cleans, so nothing
     * records an argument count and the two call sites at :972567 and :972591 are the only word on
     * it. Both pass three, and the body reads only [ebp+8] and [ebp+0xc], so under cdecl either
     * count chains correctly and the third is simply passed on to the tail call.
     *
     * The convention is what matters here. It was briefly StdCall, on the strength of the catalog
     * column, which would have popped eight bytes the caller also pops. */
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int d_texanim_draw(int index, int arg2, int arg3);

    // Three int parameters, read off the decompiled body at 0x0090bdb0: it indexes a table with
    // param_2 * 0x720 + param_1 + param_3 * 0x1c and formats a texture id from what it finds. The
    // probe used to declare it with none, so chaining called the original with three garbage stack
    // values - which is where the boot-video crashes came from.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_screen_texanim_draw(int table, int bank, int index);

    /* StdCall: setMaterialUVScroll ends in `ret 0xc` and has no plain ret at all, so the callee
     * pops the three arguments. Declared Cdecl the detour would leave them on the stack that the
     * original also does not clean, twelve bytes per call. It has never fired in a measured scene,
     * which is the only reason that has not shown up as a crash. */
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void d_uv_scroll(nint arg1, float arg2, float arg3);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_texanim_set_enable(uint arg1, uint arg2);

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_texanim_draw(int index, int arg2, int arg3)
    {
        _texanim_draw++;
        var orig = new FhCallHandle<d_texanim_draw>(new FhCallLocation(EngineAddresses.ChrTexAnimDraw, 0))
            .chain_from(h_texanim_draw).fnptr;
        return orig is null ? 0 : orig(index, arg2, arg3);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_screen_texanim_draw(int table, int bank, int index)
    {
        _screen_texanim_draw++;
        note_screen_texanim_id(table, bank, index);

        new FhCallHandle<d_screen_texanim_draw>(new FhCallLocation(EngineAddresses.ScreenTextureAnimationDraw, 0))
            .chain_from(h_screen_texanim_draw).fnptr?.Invoke(table, bank, index);
    }

    /* How often the animation this call names actually changes.
     *
     * The function builds a name with sprintf("%05d_%02d_%03d_%03d", ...) out of two words of the
     * table entry and hands it to Phyre's TextureAnimationManager, so the name is the animation and
     * the rate at which it changes is the rate the viewer sees. The call itself runs once per
     * presented frame, which says nothing on its own - what matters is whether the id advances with
     * it.
     *
     * The read mirrors the original exactly: entry = table + 0x9d9f0 + bank * 0x720 + index * 0x1c,
     * then two words at +0x24 and +0x34. Every dereference is guarded, because this runs before the
     * original has validated anything. */
    private void note_screen_texanim_id(int table, int bank, int index)
    {
        if (table == 0) return;

        nint slot = (nint)table + 0x9d9f0 + (nint)bank * 0x720 + (nint)index * 0x1c;
        int entry = *(int*)slot;
        if (entry == 0) return;

        ulong id = ((ulong)(*(uint*)((nint)entry + 0x24)) << 32) | *(uint*)((nint)entry + 0x34);

        var key = (table, bank, index);
        if (_screen_texanim_ids.TryGetValue(key, out ulong last) && last == id) return;

        _screen_texanim_ids[key] = id;
        _screen_texanim_changes++;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private void h_uv_scroll(nint arg1, float arg2, float arg3)
    {
        _uv_scroll++;
        new FhCallHandle<d_uv_scroll>(new FhCallLocation(EngineAddresses.SetMaterialUVScroll, 0))
            .chain_from(h_uv_scroll).fnptr?.Invoke(arg1, arg2, arg3);
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_texanim_set_enable(uint arg1, uint arg2)
    {
        _texanim_set_enable++;
        new FhCallHandle<d_texanim_set_enable>(new FhCallLocation(EngineAddresses.ChTextureAnimSetEnable, 0))
            .chain_from(h_texanim_set_enable).fnptr?.Invoke(arg1, arg2);
    }

}
