namespace Fahrenheit.Mods.Vrr;

public unsafe sealed partial class VrrModule
{
    private static float initial_correction_rate(uint interval, double? chosen)
        => chosen is { } rate
            ? VrrConfig.IsSupportedTargetFramerate(rate) ? (float)rate : 0f
            : interval > 0 ? 60f / interval : 0f;
}
