namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The particle spawn cadence of the twenty magic overlays that carry a clock of their own, and
///     the only correction in this module that reaches them at all.
///
///     Every one of the 581 shipped overlays exports the same InitMagicPRX, which MagicFile_Start
///     calls with the address of gMagicFunctions and which copies 32 data slots out of that table
///     into DLL-local cells at load time. Slot 764 is the address of sg_count. Twenty overlays
///     actually read the cell they cached it into, three sites each, and every site is the same
///     shape: load the pointer, <c>test byte ptr [eax], 3</c> or <c>, 7</c>, skip the body when the
///     result is non-zero. The body allocates a particle and jitters it, so the gate is an emission
///     cadence of one particle every four or every eight counts. sg_count advances once per
///     presented frame, so at 60 Hz all twenty emit at double the authored density.
///
///     Nothing else here touches them. All twenty have a stub advance, so the effect advance hold is
///     a precise no-op for them, and the gates sit in the step kernels of the overlay's own draw-side
///     particle programs, which neither the particle kernel hold nor the timeline patch reaches.
///
///     The correction is a single pointer written into slot 764 before the first overlay is loaded,
///     so no overlay is modified and no other reader of sg_count changes. That the slot can be used
///     this way is a property of the image rather than a hope: the cell has no reference anywhere in
///     FFX.exe, the table has exactly one, and FFX.exe's own sg_count readers all address the global
///     directly.
///
///     The shadow counter deliberately does not mirror sg_count's value. The gates read three bits
///     and nothing compares them against anything else, so what matters is the rate, not the phase -
///     and an overlay's phase against a counter that has been free-running since boot is arbitrary
///     in the vanilla game too.
/// </summary>
public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     The counter the twenty overlays read once the slot points here. Allocated natively
    ///     because the overlays dereference the pointer from native draw code, and never freed: an
    ///     overlay that loaded while the patch was installed keeps this address in its own cell for
    ///     as long as it stays loaded, so freeing the block at any point - including alongside the
    ///     journal's restore at process exit - would leave those cells pointing at released memory.
    /// </summary>
    private uint* _overlay_clock;

    /// <summary>
    ///     Not part of the module's success value. A refused verification leaves the twenty overlays
    ///     uncorrected, which is the state every other build of this module was in; it is not a
    ///     reason to take the whole module down.
    /// </summary>
    private void init_overlay_spawn_gate()
    {
        if (!_config.OverlaySpawnGate) return;

        // The slot carries a base relocation, so the value in it is the runtime address of sg_count
        // and not the 0x023CBBF0 the image was linked at. Comparing against the linked constant
        // would fail on every load that is not at the preferred base, which for an image marked
        // DYNAMICBASE is the ordinary case.
        nint expected = (nint)FhUtil.ptr_at<byte>(EngineAddresses.SgCount);
        nint current  = FhUtil.get_at<nint>(EngineAddresses.MagicFunctionsSgCountSlot);

        if (current != expected)
        {
            _logger.Error($"[Vrr] gMagicFunctions slot 764 at RVA 0x{EngineAddresses.MagicFunctionsSgCountSlot:X} " +
                          $"does not hold the address of sg_count; nothing written. Found 0x{current:X8}, " +
                          $"expected 0x{expected:X8}.");
            return;
        }

        _overlay_clock = (uint*)NativeMemory.AllocZeroed(sizeof(uint));

        nint counter = (nint)_overlay_clock;
        _patches.Write(EngineAddresses.MagicFunctionsSgCountSlot, new ReadOnlySpan<byte>(&counter, sizeof(nint)));

        _logger.Info($"[Vrr] Overlay spawn gate clock installed at 0x{counter:X8}; gMagicFunctions slot 764 " +
                     "repointed away from sg_count.");
    }

    /// <summary>
    ///     Advances the shadow clock once per frame a held sequence is allowed to advance, which is
    ///     the same decision every other hold in this module reads and is deliberately not derived
    ///     from Scale arithmetic of its own.
    ///
    ///     While the engine paces itself from the syncdata table Scale is 1, so no frame is held and
    ///     this advances on every pass. That is the required behaviour and not an oversight: such a
    ///     scene already runs at its authored rate, and the twenty overlays should emit exactly as
    ///     they do in the vanilla game for its duration.
    /// </summary>
    private void advance_overlay_spawn_clock()
    {
        if (_overlay_clock == null || !advance_this_frame()) return;
        (*_overlay_clock)++;
    }

    /// <summary>The shadow clock, or that the patch is not installed.</summary>
    private string overlay_spawn_gate_counts()
        => _overlay_clock == null ? "overlay_gate=off" : $"overlay_gate={*_overlay_clock}";
}
