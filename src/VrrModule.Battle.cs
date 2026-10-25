namespace Fahrenheit.Mods.Vrr;

/// <summary>
///     The battle limit timer counts frames and divides by a framerate the engine hardcodes, so it is
///     replaced rather than scaled: the original writes its result directly to globals.
/// </summary>
public unsafe sealed partial class VrrModule
{
    private bool init_battle_hooks()
    {
        if (!_config.BattleTimers) return true;

        return hook_or_log("TOBtlCtrlLimitTimer", EngineAddresses.TOBtlCtrlLimitTimer,
            () => new FhCallHandle<d_limit_timer>(new FhCallLocation(EngineAddresses.TOBtlCtrlLimitTimer, 0)).hook(this, h_limit_timer));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void d_limit_timer();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint d_rnd();

    /* Replaces the original entirely: it accumulates frames and divides by a fixed 30 or 25, which at
     * 60 Hz makes the timer run at double speed. The displayed value keeps the engine's own quirk of
     * randomising the last digit, which exists to make the timer look like it updates faster than it
     * does. Removing it would be a visible behaviour change rather than a fix. */
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private void h_limit_timer()
    {
        if (FhUtil.get_at<uint>(EngineAddresses.LimitTimerMode) != 2) return;

        float* frames  = FhUtil.ptr_at<float>(EngineAddresses.LimitTimerFrames);
        float* base_s  = FhUtil.ptr_at<float>(EngineAddresses.LimitTimerBase);
        float* raw     = FhUtil.ptr_at<float>(EngineAddresses.LimitTimerRaw);
        float* rounded = FhUtil.ptr_at<float>(EngineAddresses.LimitTimerRounded);

        *frames += 30f / TargetFramerate;
        *raw = *base_s - (*frames / 30f);

        if (*raw <= 0f)
        {
            *raw = 0;
            *rounded = 0;
            return;
        }

        uint jitter = new FhCallHandle<d_rnd>(new FhCallLocation(EngineAddresses.Rnd, 0)).fnptr!();
        uint tenths = (uint)Math.Clamp(*raw * 10, 0, uint.MaxValue);

        *rounded = ((jitter % 10) + (tenths * 10)) / 100f;
    }
}
