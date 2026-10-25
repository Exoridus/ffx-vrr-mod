namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    /// <summary>
    ///     Decided once per simulation pass and read by every hold in that pass. Catch-up passes
    ///     need separate decisions even when they share one presented image. The fractional carry
    ///     distributes held passes independently of outer presentation batching.
    /// </summary>
    private bool _advance_frame = true;
    private double _hold_carry;

    private void decide_frame_advance()
    {
        _hold_carry += 1.0 / Math.Max(1f, Scale);

        if (_hold_carry >= 1.0) { _hold_carry -= 1.0; _advance_frame = true; }
        else                    { _advance_frame = false; }
    }

    private double _vblank_carry;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_advance_vblank(float arg1);

    /* The engine advances both vertical blank counters by a hardcoded 2 per frame, its assumption of
     * two vertical blanks per 30 Hz frame. Sg_MainCalcRate turns their delta into the animation rate
     * (delta 2 -> sg_ratef 1.0), so at 60 Hz the rate stays at 1.0 and every animation advances a
     * full 30 Hz step twice as often.
     *
     * The increment sits behind menu-loop and screen-state conditions, so the delta is measured
     * rather than assumed: whatever the original added is rescaled to the selected simulation rate. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_advance_vblank(float arg1)
    {
        uint before  = FhUtil.get_at<uint>(EngineAddresses.SgVCount);
        uint before2 = FhUtil.get_at<uint>(EngineAddresses.SgVCount2);

        RenderClockAction action = render_scheduler_clock(arg1);
        if (action == RenderClockAction.Presentation) return;
        System.Threading.Interlocked.Increment(ref _fps_simulated);
        if (action == RenderClockAction.Native)
        {
            if (!_render_scheduler_active) RenderPassContext.BeginSimulation();
            decide_frame_advance();
            advance_overlay_spawn_clock();

            new FhCallHandle<d_advance_vblank>(new FhCallLocation(EngineAddresses.AdvanceVBlankCounters, 0))
                .chain_from(h_advance_vblank).fnptr!(arg1);
        }

        // Between the original's write of the multi-pass counter and Sg_MainLoop's decrement of it,
        // which is the only point where it holds the pass count for this frame. Before the delta
        // check below, because a frame that added nothing to the counters is exactly the interesting
        // case here.
        sample_frame_skip();
        if (_diagnostics?.IsCapturing == true) diagnostic_event("vblank_native", diagnostic_state());

        uint delta = FhUtil.get_at<uint>(EngineAddresses.SgVCount) - before;
        if (delta == 0) return;

        // The counters are integers and the corrected delta is not: at 60 Hz it is 1, at 120 Hz it
        // is 0.5, and rounding 0.5 up to 1 would leave every animation running at double speed with
        // no way to tell. The remainder is carried instead, so the counter advances 1, 0, 1, 0 and
        // the rate averages out exactly. That is what makes this module's correction independent of
        // the target framerate rather than tied to twice 30.
        _vblank_carry += delta / Scale;

        uint scaled = (uint)_vblank_carry;
        _vblank_carry -= scaled;

        FhUtil.set_at(EngineAddresses.SgVCount,  before  + scaled);
        FhUtil.set_at(EngineAddresses.SgVCount2, before2 + scaled);
    }

}
