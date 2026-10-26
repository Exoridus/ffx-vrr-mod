namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     Pushes polled pad history once per simulation tick. Known pressed, released and repeat
///     accessors suppress their returned edges during owned presentation passes, including older
///     history slots, without changing native input memory. Polling and overdrive sampling retain
///     their native rate. The guarded menu cache producer is held on extra images and its pressed/repeat
///     getters hide edges. Dummy and main-menu input retain native ownership. Other cache aliases,
///     transitions between ticks and timestamp-to-tick assignment remain unqualified.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private const nint PadLatch = 0x4893E0;
    private const string PadLatchPattern = "56 E8 ?? ?? ?? ?? 33 F6 6A 00 56 E8";
    private const nint PadRecords = 0xF30288, PadRecordBaseSite = 0x488EC0;
    // Overdrive windows poll and push independently on every pass for timed presses and stick rotation.
    private const nint OverdriveActive = 0xF3C930, OverdriveLatchSite = 0x49AE52;
    private const nint PadMainMenu = 0xF40824, PadMainMenuSite = 0x4AB034;
    private const nint PadMenuPause = 0xF40828, PadMenuPauseSite = 0x420B89;
    private const nint PadPressedHistory = 0x488E80, PadReleasedHistory = 0x488EA0,
        PadRepeatHistory = 0x488E50, PadRepeat2History = 0x488E30;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort d_pad_history(int port, int pad, int history);

    private const nint PadDummySelector = 0x4CA510, PadDummyActive = 0x146A430;
    private const nint PadMenuCacheUpdate = 0x4BE500, PadMenuPressed = 0x4BE4B0, PadMenuRepeat = 0x4BE470;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ushort d_menu_edge();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_pad_menu_cache_update()
    {
        if (pad_suppress_presentation_edges()) return;
        new FhCallHandle<d_render_void>(new FhCallLocation(PadMenuCacheUpdate, 0)).chain_from(h_pad_menu_cache_update).fnptr!();
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_menu_pressed()
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_menu_edge>(new FhCallLocation(PadMenuPressed, 0)).chain_from(h_pad_menu_pressed).fnptr!();
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_menu_repeat()
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_menu_edge>(new FhCallLocation(PadMenuRepeat, 0)).chain_from(h_pad_menu_repeat).fnptr!();
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    private bool _pad_latch_installed, _pad_latch_pending;
    private volatile bool _pad_history_readers_ready;
    private long _pad_latch_calls, _pad_latch_skipped, _pad_latch_deferred, _pad_latch_late;

    private string pad_latch_counts()
        => $"padlatch={_pad_latch_calls}/skip:{_pad_latch_skipped}/defer:{_pad_latch_deferred}/late:{_pad_latch_late}";

    private bool pad_input_presentation_ready() => _pad_latch_installed && _pad_history_readers_ready;

    // Timed and clock-bypassing input paths retain native publication and real-time repeat.
    private bool pad_native_input_owner()
        => EngineSyncPaced || fmv_playing() || FhUtil.get_at<int>(PadMainMenu) != 0
            || FhUtil.get_at<int>(PadMenuPause) != 0
            || FhUtil.get_at<int>(OverdriveActive) != 0 || FhUtil.get_at<byte>(PadDummyActive) != 0;

    private bool pad_suppress_presentation_edges()
        => pad_input_presentation_ready() && IsOwnedPresentationPass && !pad_native_input_owner();

    private bool init_pad_latch_hook()
    {
        _pad_latch_installed = _pad_history_readers_ready = false;
        if (!IndependentPresentationEnabled) return true;
        if (!fx_code_matches(PadLatch, PadLatchPattern)
            || !fx_code_matches(PadRecordBaseSite, "55 8B EC 8B 45 08 03 45 0C C1 E0 08 05 ?? ?? ?? ?? 5D C3")
            || !fx_operand_is(PadRecordBaseSite + 0xD, PadRecords)
            || !fx_code_matches(OverdriveLatchSite, "83 3D ?? ?? ?? ?? 00 74 14 E8 ?? ?? ?? ?? E8 ?? ?? ?? ?? E8")
            || !fx_operand_is(OverdriveLatchSite + 2, OverdriveActive)
            || !fx_call_is(OverdriveLatchSite + 0x13, PadLatch)
            || !fx_code_matches(PadMainMenuSite, "83 3D ?? ?? ?? ?? 00 74 4C")
            || !fx_operand_is(PadMainMenuSite + 2, PadMainMenu)
            || !fx_code_matches(PadMenuPauseSite, "83 3D ?? ?? ?? ?? 00 75 11")
            || !fx_operand_is(PadMenuPauseSite + 2, PadMenuPause)
            || !fx_code_matches(PadPressedHistory, "55 8B EC FF 75 10 FF 75 0C FF 75 08 E8 EF FC FF FF 66 8B 40 06 83 C4 0C 5D C3")
            || !fx_code_matches(PadReleasedHistory, "55 8B EC FF 75 10 FF 75 0C FF 75 08 E8 CF FC FF FF 66 8B 40 08 83 C4 0C 5D C3")
            || !fx_code_matches(PadRepeatHistory, "55 8B EC FF 75 10 FF 75 0C FF 75 08 E8 1F FD FF FF 66 8B 40 0A 83 C4 0C 5D C3")
            || !fx_code_matches(PadRepeat2History, "55 8B EC FF 75 10 FF 75 0C FF 75 08 E8 3F FD FF FF 66 8B 40 1C 83 C4 0C 5D C3")
            || !fx_code_matches(PadMenuCacheUpdate, "55 8B EC 51 56 66 8B 35 ?? ?? ?? ?? E8 ?? ?? ?? ?? D9 5D FC 33 C0 A3 ?? ?? ?? ?? 66 A3 ?? ?? ?? ?? A3 ?? ?? ?? ?? 66 A3 ?? ?? ?? ?? E8 ?? ?? ?? ?? 83 F8 01 75 05 E8 ?? ?? ?? ?? E8 ?? ?? ?? ??")
            || !fx_operand_is(PadMenuCacheUpdate + 8, 0x21D09E2)
            || !fx_operand_is(PadMenuCacheUpdate + 23, 0x21D09D2)
            || !fx_operand_is(PadMenuCacheUpdate + 29, 0x21D09D6)
            || !fx_operand_is(PadMenuCacheUpdate + 34, 0x21D09E2)
            || !fx_operand_is(PadMenuCacheUpdate + 40, 0x21D09E6)
            || !fx_call_is(PadMenuCacheUpdate + 0x0C, 0x230C60)
            || !fx_call_is(PadMenuCacheUpdate + 0x2C, PadDummySelector)
            || !fx_call_is(PadMenuCacheUpdate + 0x36, 0x4CA490)
            || !fx_call_is(PadMenuCacheUpdate + 0x3B, PadDummySelector)
            || !fx_code_matches(PadMenuPressed, "E8 ?? ?? ?? ?? 83 F8 01 75 07 66 A1 ?? ?? ?? ?? C3 B8 00 F0 00 00 66 85 05 ?? ?? ?? ?? 66 A1 ?? ?? ?? ?? 75 06 66 A1 ?? ?? ?? ?? C3")
            || !fx_operand_is(PadMenuPressed + 12, 0x21D09D4)
            || !fx_operand_is(PadMenuPressed + 25, 0x21D09D2)
            || !fx_operand_is(PadMenuPressed + 31, 0x21D09D4)
            || !fx_operand_is(PadMenuPressed + 39, 0x21D09E4)
            || !fx_call_is(PadMenuPressed, PadDummySelector)
            || !fx_code_matches(PadMenuRepeat, "E8 ?? ?? ?? ?? 83 F8 01 75 07 66 A1 ?? ?? ?? ?? C3 B8 00 F0 00 00 66 85 05 ?? ?? ?? ?? 66 A1 ?? ?? ?? ?? 75 06 66 A1 ?? ?? ?? ?? C3")
            || !fx_operand_is(PadMenuRepeat + 12, 0x21D09D6)
            || !fx_operand_is(PadMenuRepeat + 25, 0x21D09D2)
            || !fx_operand_is(PadMenuRepeat + 31, 0x21D09D6)
            || !fx_operand_is(PadMenuRepeat + 39, 0x21D09E6)
            || !fx_call_is(PadMenuRepeat, PadDummySelector)
            || !fx_code_matches(PadDummySelector, "0F B6 05 ?? ?? ?? ?? C3")
            || !fx_operand_is(PadDummySelector + 3, PadDummyActive))
        {
            _logger.Error("[Vrr] Pad latch, history reader or menu input bytes differ from build 25501027; pad input keeps native behavior.");
            return true;
        }
        bool ok = hook_or_log(HookSiteId.PadLatchPadLatch, "pad latch", PadLatch,
            () => new FhCallHandle<d_render_void>(new FhCallLocation(PadLatch, 0)).hook(this, h_pad_latch));
        ok &= hook_or_log(HookSiteId.PadLatchPressedHistory, "pad pressed history", PadPressedHistory,
            () => new FhCallHandle<d_pad_history>(new FhCallLocation(PadPressedHistory, 0)).hook(this, h_pad_pressed_history));
        ok &= hook_or_log(HookSiteId.PadLatchReleasedHistory, "pad released history", PadReleasedHistory,
            () => new FhCallHandle<d_pad_history>(new FhCallLocation(PadReleasedHistory, 0)).hook(this, h_pad_released_history));
        ok &= hook_or_log(HookSiteId.PadLatchRepeatHistory, "pad repeat history", PadRepeatHistory,
            () => new FhCallHandle<d_pad_history>(new FhCallLocation(PadRepeatHistory, 0)).hook(this, h_pad_repeat_history));
        ok &= hook_or_log(HookSiteId.PadLatchRepeat2History, "pad repeat2 history", PadRepeat2History,
            () => new FhCallHandle<d_pad_history>(new FhCallLocation(PadRepeat2History, 0)).hook(this, h_pad_repeat2_history));
        ok &= hook_or_log(HookSiteId.PadLatchMenuCacheUpdate, "pad menu cache update", PadMenuCacheUpdate,
            () => new FhCallHandle<d_render_void>(new FhCallLocation(PadMenuCacheUpdate, 0)).hook(this, h_pad_menu_cache_update));
        ok &= hook_or_log(HookSiteId.PadLatchMenuPressed, "pad menu pressed", PadMenuPressed,
            () => new FhCallHandle<d_menu_edge>(new FhCallLocation(PadMenuPressed, 0)).hook(this, h_pad_menu_pressed));
        ok &= hook_or_log(HookSiteId.PadLatchMenuRepeat, "pad menu repeat", PadMenuRepeat,
            () => new FhCallHandle<d_menu_edge>(new FhCallLocation(PadMenuRepeat, 0)).hook(this, h_pad_menu_repeat));
        _pad_latch_installed = _pad_history_readers_ready = ok;
        return ok;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_pressed_history(int port, int pad, int history)
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_pad_history>(new FhCallLocation(PadPressedHistory, 0))
            .chain_from(h_pad_pressed_history).fnptr!(port, pad, history);
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_released_history(int port, int pad, int history)
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_pad_history>(new FhCallLocation(PadReleasedHistory, 0))
            .chain_from(h_pad_released_history).fnptr!(port, pad, history);
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_repeat_history(int port, int pad, int history)
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_pad_history>(new FhCallLocation(PadRepeatHistory, 0))
            .chain_from(h_pad_repeat_history).fnptr!(port, pad, history);
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private ushort h_pad_repeat2_history(int port, int pad, int history)
    {
        bool suppress = pad_suppress_presentation_edges();
        ushort value = new FhCallHandle<d_pad_history>(new FhCallLocation(PadRepeat2History, 0))
            .chain_from(h_pad_repeat2_history).fnptr!(port, pad, history);
        return suppress && pad_suppress_presentation_edges() ? (ushort)0 : value;
    }

    private void pad_latch_original()
        => new FhCallHandle<d_render_void>(new FhCallLocation(PadLatch, 0)).chain_from(h_pad_latch).fnptr!();

    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_pad_latch()
    {
        _pad_latch_calls++;
        if (!_pad_latch_installed || !OwnsRenderPass || pad_native_input_owner())
        {
            pad_latch_original();
            return;
        }
        if (!_render_clock_seen) { _pad_latch_pending = true; _pad_latch_deferred++; return; }
        if (RenderPassContext.IsPresentation) { _pad_latch_skipped++; return; }
        pad_latch_original();
    }

    // The pass decision resolves an earlier latch without changing the history ring.
    private void pad_latch_pass_decided(bool presentation)
    {
        if (!_pad_latch_installed || !IsOwnedRenderLoop) return;
        bool pending = _pad_latch_pending;
        _pad_latch_pending = false;
        if (presentation && !pad_native_input_owner())
        {
            if (pending) _pad_latch_skipped++;
        }
        else if (pending) pad_latch_original();
    }

    // A loop without the clock must still publish its deferred latch once.
    private void pad_latch_end_pass()
    {
        if (!_render_inside_loop || _render_owner_thread != Environment.CurrentManagedThreadId) return;
        if (!_pad_latch_pending) return;
        _pad_latch_pending = false;
        _pad_latch_late++;
        pad_latch_original();
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
