namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     TkScanControler, the push of the polled pad record into its four-slot history ring. Each push
///     derives the just-pressed, released and repeat words of the new slot from the previous one, and
///     menu and battle input read them from the current slot on every main loop pass. A press must
///     therefore be pushed once per simulation tick and be visible to exactly one pass: pushed on a
///     presentation pass it is consumed there and the simulation pass never sees the edge, and left
///     in the slot it is read again by every presentation pass after it - one D-pad press moving the
///     cursor two or three entries at 144 Hz. Upstream FFX-60fps row UI-017 latches only on logic
///     passes; here the push also waits for the pass decision when it comes first, and presentation
///     passes see the slot's edge words cleared. The poll (TkUpdateScanControler) still runs on every
///     pass, and the held-direction repeat is wall-clock based, so neither presses nor repeats are lost.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint PadLatch = 0x4893E0;
    private const string PadLatchPattern = "56 E8 ?? ?? ?? ?? 33 F6 6A 00 56 E8";
    // Pad record array (FUN_00488ec0: (port + pad) * 0x100 + 0x01330288), ring index at +0x9C,
    // four 0x20-byte slots from +0; edge words at slot +6 pressed, +8 released, +0xA repeat, +0x1C repeat of the second word.
    private const nint PadRecords = 0xF30288, PadRecordBaseSite = 0x488EC0;
    // ODBegin. While an overdrive window is open, TOControlBtlWindow polls and pushes the pad itself on
    // every pass (cmp [ODBegin], 0 at 0x49AE52 before its own TkScanControler call) so that timed
    // presses and Lulu's stick rotation are sampled at the full rate; that latch is left alone.
    private const nint OverdriveActive = 0xF3C930, OverdriveLatchSite = 0x49AE52;
    private static readonly int[] PadEdgeOffsets = [0x6, 0x8, 0xA, 0x1C];

    private bool _pad_latch_installed, _pad_latch_pending, _pad_edges_masked;
    private readonly ushort[] _pad_edge_saved = new ushort[2 * 4];
    private readonly nint[] _pad_edge_slot = new nint[2];
    private long _pad_latch_calls, _pad_latch_skipped, _pad_latch_deferred, _pad_latch_late, _pad_edge_masks;

    private string pad_latch_counts()
        => $"padlatch={_pad_latch_calls}/skip:{_pad_latch_skipped}/defer:{_pad_latch_deferred}/late:{_pad_latch_late}/mask:{_pad_edge_masks}";

    private bool init_pad_latch_hook()
    {
        if (!_config.VrrEnabled) return true;
        if (!fx_code_matches(PadLatch, PadLatchPattern)
            || !fx_code_matches(PadRecordBaseSite, "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3")
            || !fx_operand_is(PadRecordBaseSite + 0xD, PadRecords)
            || !fx_code_matches(OverdriveLatchSite, "83 3D ?? ?? ?? ?? 00 74 14 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? E8")
            || !fx_operand_is(OverdriveLatchSite + 2, OverdriveActive)
            || !fx_call_is(OverdriveLatchSite + 0x13, PadLatch))
        {
            _logger.Error("[Vrr] TkScanControler or pad record bytes differ from build 25501027; pad input keeps latching on every pass.");
            return true;
        }
        _pad_latch_installed = hook_or_log("pad latch", PadLatch,
            () => new FhCallHandle<d_render_void>(new FhCallLocation(PadLatch, 0)).hook(this, h_pad_latch));
        return _pad_latch_installed;
    }

    private void pad_latch_original()
        => new FhCallHandle<d_render_void>(new FhCallLocation(PadLatch, 0)).chain_from(h_pad_latch).fnptr!();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_pad_latch()
    {
        _pad_latch_calls++;
        if (!_render_frame_enabled || !_render_inside_loop || FhUtil.get_at<int>(OverdriveActive) != 0)
        {
            pad_latch_original();
            return;
        }
        if (!_render_clock_seen) { _pad_latch_pending = true; _pad_latch_deferred++; return; }
        if (RenderPassContext.IsPresentation) { _pad_latch_skipped++; return; }
        pad_latch_original();
    }

    // Called once the clock has decided the pass kind.
    private void pad_latch_pass_decided(bool presentation)
    {
        if (!_pad_latch_installed) return;
        bool pending = _pad_latch_pending;
        _pad_latch_pending = false;
        if (presentation)
        {
            if (pending) _pad_latch_skipped++;
            if (FhUtil.get_at<int>(OverdriveActive) == 0) pad_mask_edges();
        }
        else if (pending) pad_latch_original();
    }

    // Main loop exit: put the edges back, and push a latch the clock never resolved (a loop without the clock).
    private void pad_latch_end_pass()
    {
        if (_pad_edges_masked)
        {
            for (int port = 0; port < 2; port++)
            {
                // The slot that was masked, which a push since then may no longer have current.
                byte* slot = (byte*)_pad_edge_slot[port];
                for (int i = 0; i < PadEdgeOffsets.Length; i++)
                    if (*(ushort*)(slot + PadEdgeOffsets[i]) == 0) *(ushort*)(slot + PadEdgeOffsets[i]) = _pad_edge_saved[port * 4 + i];
            }
            _pad_edges_masked = false;
        }
        if (!_pad_latch_pending) return;
        _pad_latch_pending = false;
        _pad_latch_late++;
        pad_latch_original();
    }

    private static byte* pad_current_slot(int port)
    {
        byte* record = FhUtil.ptr_at<byte>(PadRecords + port * 0x100);
        return record + (record[0x9C] & 3) * 0x20;
    }

    private void pad_mask_edges()
    {
        if (_pad_edges_masked) return;
        for (int port = 0; port < 2; port++)
        {
            byte* slot = pad_current_slot(port);
            _pad_edge_slot[port] = (nint)slot;
            for (int i = 0; i < PadEdgeOffsets.Length; i++)
            {
                _pad_edge_saved[port * 4 + i] = *(ushort*)(slot + PadEdgeOffsets[i]);
                *(ushort*)(slot + PadEdgeOffsets[i]) = 0;
            }
        }
        _pad_edges_masked = true;
        _pad_edge_masks++;
    }

    private byte _traced_battle_state;

    /* Measurement for the battle entry/exit delay: one line per Btl.battle_state change, with the
     * simulation and presentation counters, so the state that waits can be read off the log. */
    private void trace_battle_state()
    {
        byte state = FhUtil.get_at<byte>(EngineAddresses.BattleState);
        if (state == _traced_battle_state) return;
        _logger.Info($"[Vrr] battle_state {_traced_battle_state} -> {state}: sim_ticks={_simulation_ticks} " +
                     $"extra_images={_additional_images} gate={(_render_frame_enabled ? "open" : _render_gate_reason())} " +
                     $"filter_mask=0x{FhUtil.get_at<int>(EngineAddresses.FilterActiveMask):X}");
        _traced_battle_state = state;
    }
}
