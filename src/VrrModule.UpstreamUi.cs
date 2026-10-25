namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     UI clocks that count calls instead of time, ported from upstream FFX-60fps rows UI-014, UI-020,
///     UI-SPH-001 and UI-BTLL1. Each runs once per main loop pass, so presentation passes would advance it.
///     Every site is checked against the installed executable first; a mismatch leaves that row out.
/// </summary>
public unsafe sealed partial class VrrModule
{
    // UI-014: Ui_MinimapDraw(obj, mode), cdecl. Adds 1 to the player-arrow pulse counter obj+0x74 per call
    // (INC at +0x1206), read before the increment as a 30-call triangle wave.
    private const nint MinimapDraw = 0x51DA80;
    // UI-020: HD save/load list input, int(void). Hold counters up/down/left/right/two buttons, then two
    // press-edge counters; reports a counter of 1 or a multiple of 5 above 10.
    private const nint SaveListInput = 0x2EF320, SaveListCounters = 0x8E8184;
    // UI-SPH-001: Sphere Grid update (task 0x13), void(void), draws nothing.
    private const nint SphereGridUpdate = 0x653570;
    // UI-BTLL1: action edge test (cur & mask) && !(prev & mask) on the per-app-frame latch; the battle
    // party swap at 0x49C9FA is its only caller with mask 0x8000000.
    private const nint ActionEdge = 0x230CF0, ActionLatch = 0x8CB170;
    private const uint PartySwapMask = 0x8000000;

    private uint _partySwapSticky;
    private long _minimapHeld, _saveListHeld, _sphereSkipped, _partySwapRescued;

    private string upstream_ui_counts()
        => $"ui=minimap:{_minimapHeld} savelist:{_saveListHeld} sphere:{_sphereSkipped} l1:{_partySwapRescued}";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void d_minimap_draw(nint obj, int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int d_ui_int();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int d_action_edge(uint mask);

    private bool init_upstream_ui_hooks()
    {
        if (!_config.VrrEnabled) return true;
        bool ok = true;

        if (fx_code_matches(MinimapDraw, "53 8B DC 83 EC 08 83 E4 F0 83 C4 04 55 8B 6B 04 89 6C 24 04 8B EC 81 EC 68 03 00 00 A1 ?? ?? ?? ?? 33 C5")
            && fx_code_matches(MinimapDraw + 0x1206, "FF 46 74"))
            ok &= hook_or_log("UI-014 minimap draw", MinimapDraw,
                () => new FhCallHandle<d_minimap_draw>(new FhCallLocation(MinimapDraw, 0)).hook(this, h_minimap_draw));
        else ok &= refuse_ui_row("UI-014 minimap draw");

        if (fx_code_matches(SaveListInput, "E8 ?? ?? ?? ?? 68 00 10 00 00 A3 ?? ?? ?? ?? E8 ?? ?? ?? ?? D9 05 ?? ?? ?? ?? 83 C4 04 85 C0 75 1B A1 ?? ?? ?? ?? D8 58 04 DF E0 F6 C4 41 74 0E C7 05")
            && fx_operand_is(SaveListInput + 0x0B, SaveListCounters + 0x20)
            && fx_operand_is(SaveListInput + 0x32, SaveListCounters))
            ok &= hook_or_log("UI-020 save/load list input", SaveListInput,
                () => new FhCallHandle<d_ui_int>(new FhCallLocation(SaveListInput, 0)).hook(this, h_save_list_input));
        else ok &= refuse_ui_row("UI-020 save/load list input");

        if (fx_code_matches(SphereGridUpdate, "55 8B EC 83 EC 18 A1 ?? ?? ?? ?? 33 C5 89 45 FC 8B 0D ?? ?? ?? ?? C7 05 ?? ?? ?? ?? 01 00 00 00 80 B9 B8 15 01 00"))
            ok &= hook_or_log("UI-SPH-001 sphere grid update", SphereGridUpdate,
                () => new FhCallHandle<d_render_void>(new FhCallLocation(SphereGridUpdate, 0)).hook(this, h_sphere_grid_update));
        else ok &= refuse_ui_row("UI-SPH-001 sphere grid update");

        if (fx_code_matches(0x49C9F5, "68 00 00 00 08 E8 ?? ?? ?? ?? 83 C4 04 85 C0 74 3F 0F BF 46 08")
            && fx_call_is(0x49C9FA, ActionEdge)
            && fx_code_matches(ActionEdge, "55 8B EC FF 75 08 E8 ?? ?? ?? ?? 8B C8 E8")
            && fx_code_matches(0x231300, "B8 ?? ?? ?? ?? C3") && fx_operand_is(0x231301, ActionLatch))
            ok &= hook_or_log("UI-BTLL1 party swap edge", ActionEdge,
                () => new FhCallHandle<d_action_edge>(new FhCallLocation(ActionEdge, 0)).hook(this, h_action_edge));
        else ok &= refuse_ui_row("UI-BTLL1 party swap edge");

        return ok;
    }

    private bool refuse_ui_row(string row)
    {
        _logger.Error($"[Vrr] {row}: bytes differ from build 25501027, not installed.");
        return true;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_minimap_draw(nint obj, int mode)
    {
        bool hold = RenderPassContext.IsPresentation && obj != 0;
        uint pulse = hold ? *(uint*)(obj + 0x74) : 0;
        new FhCallHandle<d_minimap_draw>(new FhCallLocation(MinimapDraw, 0)).chain_from(h_minimap_draw).fnptr!(obj, mode);
        if (!hold) return;
        *(uint*)(obj + 0x74) = pulse;
        _minimapHeld++;
    }

    private static bool save_list_fires(int count) => count == 1 || (count > 10 && count % 5 == 0);

    /* The press edges behind counters 6 and 7 are valid for one pass only, so the call cannot be skipped.
     * It runs, a level counter that was already held gets its count back, and only a fresh press or an
     * edge is reported, in the game's own priority order. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_save_list_input()
    {
        var original = new FhCallHandle<d_ui_int>(new FhCallLocation(SaveListInput, 0)).chain_from(h_save_list_input);
        if (!RenderPassContext.IsPresentation) return original.fnptr!();

        int* counters = FhUtil.ptr_at<int>(SaveListCounters);
        int* before = stackalloc int[8];
        for (int i = 0; i < 8; i++) before[i] = counters[i];
        original.fnptr!();
        _saveListHeld++;

        Span<bool> fresh = stackalloc bool[6];
        for (int i = 0; i < 6; i++)
        {
            if (before[i] == 0) { fresh[i] = counters[i] == 1; continue; }
            if (counters[i] != 0) counters[i] = before[i];
        }
        for (int i = 0; i < 6; i++) if (fresh[i] && save_list_fires(counters[i])) return i + 1;
        if (save_list_fires(counters[6])) return 7;
        if (save_list_fires(counters[7])) return 8;
        return 0;
    }

    private readonly ushort[] _sphere_carried_edges = new ushort[2 * 4];
    private bool _sphere_edges_carried;

    /* The grid is authored for 30 Hz (divider 2 in stock) and its cursor and d-pad repeat count calls,
     * so it runs on every other simulation tick. A press pushed on a skipped tick lives in the pad
     * ring for that tick only; its edge words are carried into the slot the next run reads and put
     * back afterwards, so no press is lost. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_sphere_grid_update()
    {
        if (RenderPassContext.IsPresentation) { _sphereSkipped++; sphere_smooth(true); return; }
        if (_render_frame_enabled && !advance_this_frame())
        {
            sphere_smooth(false);
            for (int port = 0; port < 2; port++)
            {
                byte* slot = pad_current_slot(port);
                for (int i = 0; i < PadEdgeOffsets.Length; i++) _sphere_carried_edges[port * 4 + i] |= *(ushort*)(slot + PadEdgeOffsets[i]);
            }
            _sphere_edges_carried = true;
            _sphereSkipped++;
            return;
        }

        var original = new FhCallHandle<d_render_void>(new FhCallLocation(SphereGridUpdate, 0)).chain_from(h_sphere_grid_update);
        if (!_sphere_edges_carried) { original.fnptr!(); sphere_record_update(); return; }

        Span<ushort> own = stackalloc ushort[2 * 4];
        for (int port = 0; port < 2; port++)
        {
            byte* slot = pad_current_slot(port);
            for (int i = 0; i < PadEdgeOffsets.Length; i++)
            {
                ushort* word = (ushort*)(slot + PadEdgeOffsets[i]);
                own[port * 4 + i] = *word;
                *word |= _sphere_carried_edges[port * 4 + i];
            }
        }
        original.fnptr!();
        for (int port = 0; port < 2; port++)
        {
            byte* slot = pad_current_slot(port);
            for (int i = 0; i < PadEdgeOffsets.Length; i++) *(ushort*)(slot + PadEdgeOffsets[i]) = own[port * 4 + i];
        }
        Array.Clear(_sphere_carried_edges);
        _sphere_edges_carried = false;
        sphere_record_update();
    }

    // Every main loop pass: collect the party-swap presses the latch shows on this pass.
    private void party_swap_collect()
    {
        uint* latch = FhUtil.ptr_at<uint>(ActionLatch);
        _partySwapSticky |= latch[0] & ~latch[1] & PartySwapMask;
    }

    // After a simulation pass the handler has had its chance; an unconsumed press is dropped.
    private void party_swap_end_pass(bool presentation)
    {
        if (!presentation) _partySwapSticky = 0;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private int h_action_edge(uint mask)
    {
        int live = new FhCallHandle<d_action_edge>(new FhCallLocation(ActionEdge, 0)).chain_from(h_action_edge).fnptr!(mask);
        if (mask != PartySwapMask) return live;
        bool rescued = live == 0 && (_partySwapSticky & mask) != 0;
        _partySwapSticky &= ~mask;
        if (!rescued) return live;
        _partySwapRescued++;
        return 1;
    }
}
